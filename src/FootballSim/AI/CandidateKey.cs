namespace FootballSim.AI;

/// <summary>
/// What kind of act a candidate represents. This is the coarse half of candidate identity; the target
/// and variant complete it (see <see cref="CandidateKey"/>).
///
/// It exists as an enum rather than more string prefixes because the decision path needs to compare and
/// branch on it every tick, and because <c>actionName.StartsWith("Job:")</c> is exactly the kind of
/// stringly-typed coupling that breaks silently when a label is reworded.
/// </summary>
public enum CandidateKind
{
    /// <summary>A candidate built without a key. Never selected deliberately; treated as its own identity.</summary>
    Unknown = 0,

    /// <summary>Strike at the opponent goal.</summary>
    Shot,

    /// <summary>Play the ball to a team-mate (identity carries the receiver and the pass type).</summary>
    Pass,

    /// <summary>Carry the ball into space.</summary>
    Carry,

    /// <summary>Close down / tackle the carrier.</summary>
    Press,

    /// <summary>Attack the ray of a pass in flight.</summary>
    Intercept,

    /// <summary>Occupy the structurally correct position (support lane or defensive zone).</summary>
    Shape,

    /// <summary>Second-defender body challenge on the carrier.</summary>
    Challenge,

    /// <summary>Movement toward a persistent team job target.</summary>
    Job,

    /// <summary>Take the nearest opponent on with the ball (A4.1), as opposed to carrying into space.</summary>
    Dribble,

    /// <summary>Hold the ball with the body between it and the nearest opponent (A4.1).</summary>
    Shield,

    /// <summary>Strike the ball long and high out of danger (A4.1).</summary>
    Clearance,

    /// <summary>
    /// Move along an off-ball run path (A5.1/A5.2), as opposed to occupying a job anchor. The distinction
    /// matters: an anchor is a place a duty wants covered, a run has a meeting region and a time window,
    /// and only the run can be adopted for its whole life (<see cref="RunIntent"/>).
    /// </summary>
    Run
}

/// <summary>
/// Stable identity of an action candidate: kind + target + variant.
///
/// Why this exists: action names were carrying identity. <c>Pass(GroundPass)To#9</c> is a label, not an
/// identity — it names a receiver in prose, it changes when a string is reworded, and it cannot express
/// two variants aimed at the same target. The plan requires identity to survive relabelling and to be
/// independent of a score or a floating-point coordinate, so an ongoing commitment can be matched
/// reliably even while the target point moves by centimetres or the utility wobbles.
///
/// The struct is cheap and allocation-free by construction: <see cref="TargetPlayerId"/> is an int and
/// <see cref="TargetTag"/> is expected to be an interned literal ("zone", "goal", "tackle"). The
/// human-readable form is produced only when a trace or a debug summary asks for it.
/// </summary>
public readonly record struct CandidateKey(
    CandidateKind Kind,
    int TargetPlayerId = 0,
    string TargetTag = "",
    bool Adjusted = false)
{
    /// <summary>The identity used for a candidate that was created without one.</summary>
    public static CandidateKey None => default;

    /// <summary>True when this key carries no identity at all.</summary>
    public bool IsNone => Kind == CandidateKind.Unknown && TargetPlayerId == 0 && string.IsNullOrEmpty(TargetTag);

    /// <summary>Same kind and target, with the adjusted-target variant flag changed.</summary>
    public CandidateKey AsAdjusted(bool adjusted) => this with { Adjusted = adjusted };

    /// <summary>Stable text form, e.g. <c>Pass:#9:GroundPass</c>, <c>Job:Marker:adj</c>, <c>Shot:goal</c>.</summary>
    public string Describe()
    {
        string kind = Kind.ToString();
        string target = TargetPlayerId > 0
            ? $"#{TargetPlayerId}"
            : TargetTag;
        bool hasVariant = !string.IsNullOrEmpty(TargetTag) && TargetPlayerId > 0;

        string text = hasVariant
            ? $"{kind}:{target}:{TargetTag}"
            : string.IsNullOrEmpty(target) ? kind : $"{kind}:{target}";

        return Adjusted ? $"{text}:adj" : text;
    }

    public override string ToString() => Describe();
}
