using System;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// A7.5: the four error sources, separated and numbered — one value each, one consumer each.
///
/// Before this, error was implicit everywhere: <see cref="PerceptionSystem"/> added positional noise scaled
/// by <i>distance</i>, the ball view was delayed by the <i>attribute</i> alone, the scorers multiplied by
/// fatigue only through the movement speed, and the contact layer applied pressure only to a kick's
/// accuracy. Nothing tied the four together, so a "tired player" was tired only in his legs.
///
/// <b>This is the one place the shared causes are read.</b> Stamina, composure and the technical attributes
/// are converted here into three numbers for a player's <i>picture</i> of the game
/// (<see cref="ObservationNoiseScale"/>, <see cref="EvaluationError01"/>, <see cref="ReactionDelaySeconds"/>)
/// and one for his <i>contact</i> with the ball (<see cref="PlayerCognitiveState.ExecutionError01"/>, which
/// needs the pressure at contact and so is computed where the contact happens). No consumer invents its own
/// fatigue or composure term for these four effects; they read these numbers (architecture rule 6).
///
/// <b>The neutral point is the default athlete at full stamina</b> — composure at
/// <see cref="PlayerCognitiveState.ComposureReference01"/> and stamina 1.0 produce a scale of exactly 1, an
/// evaluation error of exactly 0 and the attribute's own reaction delay, so a fixture that does not fatigue
/// a player executes the pre-A7.5 build bit for bit. Error appears only when a shared cause moves.
///
/// <b>Not modelled here.</b> Attention (A7.1) decides what is <i>seen</i>, and memory (A2.2) decides how a
/// stale sighting ages; both already publish their own uncertainty. This module describes the observer, not
/// the observation, and it never reads an opponent's position.
/// </summary>
public readonly record struct CognitiveState(
    float ObservationNoiseScale,
    float EvaluationError01,
    float ReactionDelaySeconds)
{
    /// <summary>The clear-headed, fresh athlete: no extra noise, no misjudgement, the attribute's delay.</summary>
    public static CognitiveState Neutral(float reactionDelaySeconds) => new(1f, 0f, reactionDelaySeconds);

    /// <summary>True when this state adds no error at all (the pre-A7.5 build).</summary>
    public bool IsNeutral => ObservationNoiseScale == 1f && EvaluationError01 == 0f;

    /// <summary>Compact text form for a trace or a probe column.</summary>
    public string Describe() =>
        $"noise x{ObservationNoiseScale:F2}, evaluation {EvaluationError01:F2}, delay {ReactionDelaySeconds:F3}s";
}

/// <summary>
/// A7.5: derives <see cref="CognitiveState"/> and the execution-error number from the shared causes.
///
/// <b>One owner per source.</b>
/// <list type="bullet">
/// <item><b>Observation error</b> — <see cref="PerceptionSystem"/> multiplies its positional and ball noise
/// by <see cref="CognitiveState.ObservationNoiseScale"/>: a tired player's picture is blurrier, and because
/// the noise is the same Gaussian draw scaled up, the blur is a statement about him, not about the
/// opponent.</item>
/// <item><b>Evaluation error</b> — <see cref="TacticalUtilityAiController"/> adds
/// <see cref="PlayerCognitiveState.EvaluationBias"/> × <see cref="CognitiveState.EvaluationError01"/> ×
/// <see cref="EvaluationUtilitySpan"/> to each candidate's utility: a tired or rattled player misreads the
/// value of what he is choosing between, so a close call can go the other way and an easy call cannot.</item>
/// <item><b>Reaction latency</b> — the delayed view of the ball in <see cref="PerceptionSystem"/> uses
/// <see cref="CognitiveState.ReactionDelaySeconds"/> instead of the raw attribute, so fatigue lengthens the
/// lag (bounded by the perception history's own limit).</item>
/// <item><b>Execution error</b> — <see cref="PlayerCognitiveState.ExecutionError01"/> is applied to the
/// first-touch quality in <c>FootballerLocomotion</c>: pressure and fatigue make the touch worse, which in
/// A7.4's model means less of the planned touch is realised and more of the pass's pace survives. This is
/// the A7 exit gate's "pressure reduces control quality" clause, with one owner.</item>
/// </list>
///
/// The evaluation bias is a <b>deterministic hash of (player, tick, candidate)</b> rather than a draw from
/// the reserved <see cref="RandomStream.Decision"/> stream, on purpose: a stream's advancement would then
/// depend on how many candidates were scored, so adding or removing one candidate anywhere would shift every
/// later draw for that player. The hash keeps the error a property of the decision, not of a call order, and
/// the decision path consumes no simulation randomness.
/// </summary>
public static class PlayerCognitiveState
{
    /// <summary>
    /// Composure at which a player's judgement is unimpaired. It is the default
    /// <see cref="PlayerAttributes.Composure01"/>, so an unconfigured athlete carries no composure penalty
    /// and the error appears only for a player who is actually below the reference.
    /// </summary>
    public const float ComposureReference01 = 0.80f;

    /// <summary>Extra positional noise at zero stamina, as a fraction of the distance-based noise.</summary>
    public const float FatigueObservationNoiseGain = 0.45f;

    /// <summary>Lengthening of the ball-view delay at zero stamina, as a fraction of the attribute's delay.</summary>
    public const float FatigueReactionGain = 0.60f;

    /// <summary>Evaluation error at zero stamina (0..1).</summary>
    public const float FatigueEvaluationGain = 0.55f;

    /// <summary>Evaluation error per unit of composure below <see cref="ComposureReference01"/>.</summary>
    public const float ComposureEvaluationGain = 1.20f;

    /// <summary>Utility points the evaluation error can move a candidate at error 1 (utility scale ~0..1).</summary>
    public const float EvaluationUtilitySpan = 0.08f;

    /// <summary>First-touch quality lost at full contact pressure by a player with no composure at all.</summary>
    public const float PressureExecutionGain = 0.90f;

    /// <summary>First-touch quality lost at zero stamina on top of the pressure term.</summary>
    public const float FatigueExecutionGain = 0.20f;

    /// <summary>
    /// The player's picture-of-the-game error, from stamina (fatigue) and composure (calm).
    /// </summary>
    public static CognitiveState Compute(in PlayerAttributes attributes, float stamina01)
    {
        float fatigue = 1f - Math.Clamp(stamina01, 0f, 1f);
        float composure = Math.Clamp(attributes.Composure01, 0f, 1f);
        float composureGap = MathF.Max(0f, ComposureReference01 - composure);

        float observationScale = 1f + FatigueObservationNoiseGain * fatigue;
        float evaluationError = Math.Clamp(
            FatigueEvaluationGain * fatigue + ComposureEvaluationGain * composureGap, 0f, 1f);
        float reactionDelay = attributes.ReactionDelaySeconds * (1f + FatigueReactionGain * fatigue);

        return new CognitiveState(observationScale, evaluationError, reactionDelay);
    }

    /// <summary>
    /// The first-touch quality a contact costs this player: pressure at the contact (0..1) and fatigue,
    /// relieved by composure. Zero when there is no pressure and no fatigue, so an untouched, fresh player's
    /// first touch is unchanged.
    /// </summary>
    public static float ExecutionError01(in PlayerAttributes attributes, float stamina01, float pressure01)
    {
        float fatigue = 1f - Math.Clamp(stamina01, 0f, 1f);
        float composure = Math.Clamp(attributes.Composure01, 0f, 1f);
        float pressure = Math.Clamp(pressure01, 0f, 1f);

        return Math.Clamp(
            PressureExecutionGain * pressure * (1f - composure) + FatigueExecutionGain * fatigue, 0f, 1f);
    }

    /// <summary>
    /// A deterministic draw in [-1, 1] for one candidate in one decision. Stable for a given
    /// (player, tick, key) on every platform: an FNV-style mix over the key's own fields, never a string
    /// hash (which .NET randomises per process) and never a random stream (which would couple the error to
    /// the number of candidates scored).
    /// </summary>
    public static float EvaluationBias(int playerId, int tick, in CandidateKey key)
    {
        uint hash = 2166136261u;
        hash = Mix(hash, (uint)key.Kind);
        hash = Mix(hash, (uint)key.TargetPlayerId);
        hash = Mix(hash, key.Adjusted ? 1u : 0u);
        string tag = key.TargetTag;
        for (int i = 0; i < tag.Length; i++)
            hash = Mix(hash, tag[i]);
        hash = Mix(hash, (uint)playerId);
        hash = Mix(hash, (uint)tick);

        // Avalanche the result before using it: FNV's multiply spreads low bits upward, so the low 24 bits
        // of the running hash are still strongly correlated with a small sequential input like the tick.
        // Without this the "error" leaned one way instead of being a draw around zero.
        hash ^= hash >> 15;
        hash *= 0x2C1B3C6Du;
        hash ^= hash >> 12;
        hash *= 0x297A2D39u;
        hash ^= hash >> 15;

        return (hash & 0x00FFFFFFu) / (float)0x00800000u - 1f;
    }

    private static uint Mix(uint hash, uint value) => (hash ^ value) * 16777619u;
}
