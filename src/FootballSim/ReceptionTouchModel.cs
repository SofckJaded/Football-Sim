using System;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Locomotion;

/// <summary>
/// A7.4: what a <i>legal</i> first touch does differently because the receiver planned it.
///
/// <b>One owner, one seam.</b> This model is the only place where a <see cref="ReceptionIntent"/> turns
/// into ball physics. It is consulted by the execution layer's first-touch branch
/// (<c>FootballerLocomotion.ExecuteBallContact</c>) <i>after</i> reach, contact height, body orientation,
/// recovery and the contact arbitration have already allowed the contact: the intent can change the
/// direction and the pace of a contact that happened, never whether one happens, where the body is, or
/// which player wins the ball.
///
/// <b>Control quality is the floor, not the intent.</b> The neutral cushion the execution layer already
/// computed stays the base, and the intended touch is realised in proportion to the receiver's own
/// first-touch quality (<c>0.50·FirstTouch01 + 0.30·Balance01 + 0.20·ControlQuality01</c>). A receiver
/// with a poor first touch moves only a fraction of the way from "the ball runs off him" toward "the
/// touch he wanted"; intending a good touch is not a way to have one. No second fatigue, balance or
/// pressure term is applied here — execution already owns those numbers (no duplicate penalties).
///
/// <b>Every shape is bounded.</b> A planned touch can add at most its own <c>CapBonus</c> over the cap
/// the neutral cushion already had, and that allowance may never take the ball further past the
/// receiver than the existing dribble spring allows (<c>player + 2.6 m/s</c>). The killing touches
/// only ever take pace <i>off</i>: they are granted no allowance at all.
/// </summary>
public static class ReceptionTouchModel
{
    /// <summary>
    /// The three numbers that distinguish one planned touch from another.
    /// </summary>
    /// <param name="RetentionScale">
    /// Multiplier on the pace the neutral cushion would have kept from the pass. Below 1 is a killing
    /// touch, above 1 lets the ball run.
    /// </param>
    /// <param name="PushSpeed">Metres per second the touch adds along the planned touch direction.</param>
    /// <param name="CapBonus">
    /// Extra speed allowance over the neutral first-touch cap, so a deliberately forward touch can
    /// actually lead the ball. Zero for the touches that hold the ball.
    /// </param>
    public readonly record struct TouchShape(float RetentionScale, float PushSpeed, float CapBonus);

    /// <summary>
    /// The shape of each planned touch. Stated constants, not fitted values: a change here is a
    /// behaviour change (bump <see cref="SimulationVersion.AiBehaviour"/>).
    ///
    /// Kill and shield take pace away; open body only turns the touch up the pitch; stride and escape
    /// keep the incoming pace and add a bounded push into space.
    /// </summary>
    public static TouchShape ShapeOf(ReceptionTouchKind kind) => kind switch
    {
        ReceptionTouchKind.Kill => new TouchShape(RetentionScale: 0.20f, PushSpeed: 0f, CapBonus: 0f),
        ReceptionTouchKind.OpenBody => new TouchShape(RetentionScale: 0.35f, PushSpeed: 0.35f, CapBonus: 0.2f),
        ReceptionTouchKind.Shield => new TouchShape(RetentionScale: 0.30f, PushSpeed: 0.10f, CapBonus: 0.1f),
        ReceptionTouchKind.StrideTouch => new TouchShape(RetentionScale: 0.85f, PushSpeed: 1.60f, CapBonus: 1.0f),
        ReceptionTouchKind.EscapePressure => new TouchShape(RetentionScale: 0.95f, PushSpeed: 1.90f, CapBonus: 1.2f),
        _ => new TouchShape(RetentionScale: 1f, PushSpeed: 0f, CapBonus: 0f)
    };

    /// <summary>
    /// The largest relative ball speed any planned touch may produce. It is the existing dribble
    /// spring's own allowance (<c>player + 2.6 m/s</c>), so a reception touch can never launch the ball
    /// harder than a dribble touch already can.
    /// </summary>
    public const float MaxRelativeTouchSpeed = 2.6f;

    /// <summary>
    /// How far the ball can be from the planned meeting point before the plan counts as a wrong guess. The
    /// contact happens anywhere inside the receiving reach (~1.2 m for a receive) and the body is a step away
    /// from its own meeting point, so ordinary contact geometry puts the ball a metre off the plan; this
    /// tolerance sits beyond a full reach so only a genuinely different arrival counts.
    /// </summary>
    public const float AnticipationToleranceMeters = 1.6f;

    /// <summary>
    /// How far off the plan has to be for the anticipation error to be complete (an unrehearsed touch).
    /// Twice the tolerance, so the mechanism ramps over a range a mis-predicted flight actually occupies.
    /// </summary>
    public const float AnticipationFullErrorMeters = 3.2f;

    /// <summary>
    /// How much of the intended touch a completely wrong guess costs. Not 1: the touch still happens and
    /// still follows the body's intent, it just cannot be the rehearsed one.
    /// </summary>
    public const float AnticipationPenalty01 = 0.6f;

    /// <summary>
    /// Resolves the ball velocity a legal first touch produces.
    /// </summary>
    /// <param name="intent">The planned touch. A neutral intent returns <paramref name="neutralCushioned"/> unchanged.</param>
    /// <param name="neutralCushioned">
    /// What the pre-A7.4 cushion produced: <c>player·0.95 + settle + retained</c>, already clamped to
    /// <paramref name="neutralCap"/>. This is the base the intent moves <i>away from</i>.
    /// </param>
    /// <param name="playerVelXZ">The receiver's own horizontal velocity.</param>
    /// <param name="incomingXZ">The arriving ball's horizontal velocity, before the touch.</param>
    /// <param name="controlQuality01">The execution layer's first-touch quality for this receiver.</param>
    /// <param name="touchDirectionXZ">
    /// Where the touch should send the ball: the plan's body facing. Must not be zero (the caller passes
    /// the command's facing, which the AI always sets).
    /// </param>
    /// <param name="neutralCap">The neutral first-touch speed cap the caller already applied.</param>
    public static Vector2 Resolve(
        in ReceptionIntent intent,
        Vector2 neutralCushioned,
        Vector2 playerVelXZ,
        Vector2 incomingXZ,
        float controlQuality01,
        Vector2 touchDirectionXZ,
        float neutralCap,
        float anticipationErrorMeters = 0f)
    {
        if (!intent.IsPlanned) return neutralCushioned;

        float quality = Math.Clamp(controlQuality01, 0f, 1f);
        TouchShape shape = ShapeOf(intent.TouchKind);

        // A7.5: the plan was an early guess about where the ball would be. When the contact happens
        // somewhere else, the touch the receiver rehearsed is not the touch the contact allows — the less
        // so, the further the anticipation was off. Zero error (the normal case) changes nothing.
        float anticipation01 = Math.Clamp(
            (anticipationErrorMeters - AnticipationToleranceMeters) /
            (AnticipationFullErrorMeters - AnticipationToleranceMeters), 0f, 1f);
        float realisation = quality * (1f - AnticipationPenalty01 * anticipation01);
        if (realisation <= 0f) return neutralCushioned;

        Vector2 direction = touchDirectionXZ.LengthSquared() > 1e-6f
            ? Vector2.Normalize(touchDirectionXZ)
            : Vector2.Zero;

        // The touch the receiver wanted: his own run, the planned push, and as much of the incoming
        // pace as the kind is willing to keep.
        Vector2 intended = playerVelXZ * 0.95f
                           + direction * shape.PushSpeed
                           + incomingXZ * ((1f - quality) * 0.30f * shape.RetentionScale);

        // Realised only as far as his first touch (and his anticipation) allows: a poor touch, or a plan
        // built on the wrong meeting point, stays a poor touch.
        Vector2 blended = neutralCushioned + (intended - neutralCushioned) * realisation;

        // The allowance may add speed, never take any away: a stride touch on a hard pass must not be
        // clamped below what the neutral cushion already produced. The dribble spring's bound is the
        // ceiling for the added part only.
        float dribbleBound = playerVelXZ.Length() + MaxRelativeTouchSpeed;
        float cap = MathF.Max(neutralCap, MathF.Min(neutralCap + shape.CapBonus, dribbleBound));
        return ClampLength(blended, cap);
    }

    private static Vector2 ClampLength(Vector2 v, float maxLength)
    {
        float length = v.Length();
        if (!float.IsFinite(length) || length <= maxLength || length <= 1e-5f) return v;
        return v * (maxLength / length);
    }
}
