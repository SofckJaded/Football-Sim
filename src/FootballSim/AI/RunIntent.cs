using System;
using System.Numerics;
using FootballSim.Tactics;

namespace FootballSim.AI;

/// <summary>
/// The bounded vocabulary of off-ball paths (A5.1). Before this, an off-ball player had one target: the
/// anchor point of his assigned team job. A job is *what he is for*; a path is *where he goes and when*
/// — and a run that means to meet a ball needs a meeting region and a time window, not a timeless point.
///
/// The list is deliberately short and ordered from support to speculation, which is the plan's
/// instruction for A5.1: hold width, check short, in behind, overlap, underlap, recycle support,
/// late/blind-side box arrival, pin, then decoy. <see cref="Decoy"/> is declared here because it is part
/// of the vocabulary, but it is not generated yet: its value is the bounded follow-versus-hold defender
/// response, which is A5.6's job. A path with no price would only add a candidate that can never win.
/// </summary>
public enum RunKind
{
    /// <summary>Possession: stay on the touchline in the player's own lane, onside.</summary>
    HoldWidth,

    /// <summary>Possession: drop toward the ball to give the carrier a shorter, safer pass.</summary>
    CheckShort,

    /// <summary>Possession: attack the space between the last line and the offside line.</summary>
    InBehind,

    /// <summary>Possession: go outside a wide carrier, beyond him toward the byline.</summary>
    Overlap,

    /// <summary>Possession: go inside a wide carrier, into the half-space behind him.</summary>
    Underlap,

    /// <summary>Possession: fall in goal-side of the ball as the safe way back out.</summary>
    RecycleSupport,

    /// <summary>Possession: time a box arrival on the player's own side of the box.</summary>
    LateBoxArrival,

    /// <summary>Possession: arrive in the box on the far side of the man marking the space.</summary>
    BlindSideArrival,

    /// <summary>Possession: hold the last defender, staying onside, so the line cannot drop early.</summary>
    Pin,

    /// <summary>Possession: pull a defender away from a team-mate's space. Not generated yet (A5.6).</summary>
    Decoy
}

/// <summary>
/// One off-ball path (A5.1/A5.2): where it starts, the region it means to receive in, and when.
///
/// A path is at most two segments, described by a <b>hold point</b> and a <b>meeting region</b>. The hold
/// point is what makes timing real: a late box arrival holds at the edge of the box until
/// <see cref="HoldUntilTick"/> and only then attacks the region, instead of standing in the six-yard box
/// for two seconds. There is no third waypoint on purpose — a run that needs a slalom is a dribble, and
/// the execution layer steers every tick anyway.
///
/// The windows are absolute engine ticks (like every other timer in the AI), never seconds since some
/// local event, so a paused or restarted tick cannot stretch them.
/// </summary>
public readonly record struct RunPath(
    RunKind Kind,
    Vector2 StartXZ,
    Vector2 MeetXZ,
    float MeetRadiusMeters,
    int StartTick,
    int MeetTick,
    int ExpiresTick,
    int HoldUntilTick,
    Vector2 HoldXZ,
    float Confidence01)
{
    /// <summary>True while the path is being *timed* rather than chased: the hold point is the target.</summary>
    public bool IsHolding(int tick) => tick < HoldUntilTick;

    /// <summary>True while this path may still be met by a pass.</summary>
    public bool IsLive(int tick) => tick <= ExpiresTick;

    /// <summary>
    /// Where the player should be putting his feet right now: the hold point while the run is timed, the
    /// meeting region once it is released. This is what makes "late arrival" a real behaviour instead of
    /// a label on a point the player sprints to immediately.
    /// </summary>
    public Vector2 TargetFor(Vector2 selfXZ, int tick) => tick < HoldUntilTick ? HoldXZ : MeetXZ;

    /// <summary>
    /// Direction of the run over its whole length — what the receiver's body is doing when the ball
    /// arrives, and one of the A5.2 score terms. Falls back to the attacking direction for a degenerate
    /// path so a scorer never reads a NaN.
    /// </summary>
    public Vector2 RunDirectionXZ(float attackDirX)
    {
        Vector2 direction = MeetXZ - StartXZ;
        float lengthSquared = direction.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-6f)
            return new Vector2(attackDirX, 0f);
        return direction / MathF.Sqrt(lengthSquared);
    }

    /// <summary>Compact text form for a trace line or a failure message.</summary>
    public string Describe() =>
        $"{Kind}({StartXZ.X:F0},{StartXZ.Y:F0})→({MeetXZ.X:F0},{MeetXZ.Y:F0}) " +
        $"t{StartTick}..{ExpiresTick} meet {MeetTick}";
}

/// <summary>
/// The path a player has actually adopted, and the duty he adopted it under (A5.2).
///
/// Adoption is what makes a run a run: the meeting region is <b>frozen</b> for the life of the intent, so
/// the target does not chase the ball and the player does not re-decide every tick which space he is
/// attacking. The intent ends on its expiry, or when the duty changes, the ball leaves, the region is no
/// longer legally receivable, or the player simply chooses something else. A5.3 advertises this object
/// (with a communication delay) to the carrier; before that, it is local to the runner.
/// </summary>
public readonly record struct RunIntent(RunPath Path, TacticalJob Duty, int AdoptedTick)
{
    public RunKind Kind => Path.Kind;

    /// <summary>How sure the runner is that the space will still be there (A5.3 advertises this).</summary>
    public float Confidence01 => Path.Confidence01;

    /// <summary>True while this intent is still within its own window.</summary>
    public bool IsLive(int tick) => tick <= Path.ExpiresTick;

    /// <summary>Ticks the intent has been held. Diagnostics and tests.</summary>
    public int AgeTicks(int tick) => Math.Max(0, tick - AdoptedTick);

    public string Describe() => $"{Path.Describe()} [{Duty}] held {AdoptedTick}";
}

/// <summary>
/// Stable identities for run candidates. The tags are interned literals and the action names are fixed
/// per kind, because a candidate is built every tick for up to five players and allocating a formatted
/// string per path would put the diagnostics in the hot path.
/// </summary>
public static class RunTags
{
    /// <summary>The <see cref="CandidateKey"/> target tag for a run kind.</summary>
    public static string TagOf(RunKind kind) => kind switch
    {
        RunKind.HoldWidth => "HoldWidth",
        RunKind.CheckShort => "CheckShort",
        RunKind.InBehind => "InBehind",
        RunKind.Overlap => "Overlap",
        RunKind.Underlap => "Underlap",
        RunKind.RecycleSupport => "RecycleSupport",
        RunKind.LateBoxArrival => "LateBoxArrival",
        RunKind.BlindSideArrival => "BlindSide",
        RunKind.Pin => "Pin",
        _ => "Decoy"
    };

    /// <summary>Candidate identity of a run: kind <see cref="CandidateKind.Run"/> plus the kind's tag.</summary>
    public static CandidateKey KeyOf(RunKind kind) => new(CandidateKind.Run, TargetTag: TagOf(kind));

    /// <summary>Display name of a run candidate.</summary>
    public static string ActionNameOf(RunKind kind) => kind switch
    {
        RunKind.HoldWidth => "Run:HoldWidth",
        RunKind.CheckShort => "Run:CheckShort",
        RunKind.InBehind => "Run:InBehind",
        RunKind.Overlap => "Run:Overlap",
        RunKind.Underlap => "Run:Underlap",
        RunKind.RecycleSupport => "Run:RecycleSupport",
        RunKind.LateBoxArrival => "Run:LateBox",
        RunKind.BlindSideArrival => "Run:BlindSide",
        RunKind.Pin => "Run:Pin",
        _ => "Run:Decoy"
    };
}
