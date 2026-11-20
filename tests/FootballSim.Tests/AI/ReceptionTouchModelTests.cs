using System;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.4 — what a planned touch does to a first touch that execution has already allowed.
///
/// The model is pure, so these drive it directly: one legal contact's neutral cushion goes in, the
/// ball velocity the contact produces comes out. The three rules the task states are pinned here —
/// the five kinds are distinguishable, the neutral intent is the pre-A7.4 cushion exactly, and the
/// receiver's own first-touch quality is the floor (a poor touch realises less of what he planned).
/// Whether a contact happens at all is not this model's business, and is proven in
/// <see cref="ReceptionIntentExecutionTests"/>.
/// </summary>
public sealed class ReceptionTouchModelTests
{
    // A realistic reception: the receiver is running at 4 m/s along +X, the pass arrives at his feet
    // at 6 m/s against him, and his first-touch quality is good but not perfect.
    private static readonly Vector2 PlayerVelocity = new(4f, 0f);
    private static readonly Vector2 Incoming = new(-6f, 0f);
    private static readonly Vector2 TouchDirection = new(1f, 0f);
    private const float Quality = 0.75f;

    /// <summary>The neutral cushion of the same fixture, computed the way execution computes it.</summary>
    private static Vector2 NeutralCushion(float quality)
    {
        Vector2 retained = Incoming * ((1f - quality) * 0.30f);
        Vector2 settle = TouchDirection * 0.6f; // a ball at the feet: settle speed in [0.35, 0.9]
        return PlayerVelocity * 0.95f + settle + retained;
    }

    private static float NeutralCap(float quality) =>
        PlayerVelocity.Length() + 1.2f + (1f - quality) * Incoming.Length() * 0.15f;

    private static Vector2 Resolve(ReceptionTouchKind kind, float quality = Quality) =>
        ReceptionTouchModel.Resolve(
            new ReceptionIntent(kind), NeutralCushion(quality), PlayerVelocity, Incoming, quality,
            TouchDirection, NeutralCap(quality));

    /// <summary>How fast the ball travels relative to the receiver, signed along his touch direction.</summary>
    private static float RelativeAlongTouch(Vector2 ballVelocity) =>
        Vector2.Dot(ballVelocity - PlayerVelocity, TouchDirection);

    [Fact]
    public void TheNeutralIntent_IsTheExactPreA74Cushion()
    {
        Vector2 neutral = NeutralCushion(Quality);
        Vector2 resolved = ReceptionTouchModel.Resolve(
            ReceptionIntent.None, neutral, PlayerVelocity, Incoming, Quality, TouchDirection,
            NeutralCap(Quality));

        Assert.Equal(neutral, resolved);
        Assert.False(ReceptionIntent.None.IsPlanned);
        Assert.False(default(FootballerCommand).ReceptionIntent.IsPlanned);
    }

    [Fact]
    public void Kill_DoesNotLetTheBallRunAway_AStrideTouchDoes()
    {
        float kill = RelativeAlongTouch(Resolve(ReceptionTouchKind.Kill));
        float stride = RelativeAlongTouch(Resolve(ReceptionTouchKind.StrideTouch));

        Assert.True(kill < 0.2f,
            $"the kill let the ball run {kill:F2} m/s ahead of the receiver instead of holding it");
        Assert.True(stride > 0.5f,
            $"the stride touch only put the ball {stride:F2} m/s ahead of a 4 m/s run");
        Assert.True(stride - kill > 0.5f,
            $"kill and stride differ by only {stride - kill:F2} m/s; the intent is not shaping the touch");
    }

    [Fact]
    public void TheFiveKinds_AreOrderedByHowMuchTheyLetTheBallRun()
    {
        float kill = RelativeAlongTouch(Resolve(ReceptionTouchKind.Kill));
        float shield = RelativeAlongTouch(Resolve(ReceptionTouchKind.Shield));
        float openBody = RelativeAlongTouch(Resolve(ReceptionTouchKind.OpenBody));
        float stride = RelativeAlongTouch(Resolve(ReceptionTouchKind.StrideTouch));
        float escape = RelativeAlongTouch(Resolve(ReceptionTouchKind.EscapePressure));

        Assert.True(kill <= shield, $"kill {kill:F2} should not outrun shield {shield:F2}");
        Assert.True(shield < openBody, $"shield {shield:F2} should not outrun open body {openBody:F2}");
        Assert.True(openBody < stride, $"open body {openBody:F2} should not outrun stride {stride:F2}");
        Assert.True(stride < escape, $"stride {stride:F2} should not outrun escape {escape:F2}");
    }

    [Fact]
    public void TheNeutralShape_IsTheNoOpShape()
    {
        ReceptionTouchModel.TouchShape shape = ReceptionTouchModel.ShapeOf(ReceptionTouchKind.None);
        Assert.Equal(1f, shape.RetentionScale);
        Assert.Equal(0f, shape.PushSpeed);
        Assert.Equal(0f, shape.CapBonus);
    }

    [Fact]
    public void ZeroQuality_RealisesNoneOfTheIntent()
    {
        Vector2 neutral = NeutralCushion(0f);
        foreach (ReceptionTouchKind kind in Enum.GetValues<ReceptionTouchKind>())
        {
            Vector2 resolved = ReceptionTouchModel.Resolve(
                new ReceptionIntent(kind), neutral, PlayerVelocity, Incoming, 0f, TouchDirection,
                NeutralCap(0f));
            Assert.Equal(neutral, resolved);
        }
    }

    [Fact]
    public void APoorFirstTouch_RealisesLessOfTheOrderedTouch_ThanAGoodOne()
    {
        float poor = RelativeAlongTouch(Resolve(ReceptionTouchKind.StrideTouch, quality: 0.3f));
        float good = RelativeAlongTouch(Resolve(ReceptionTouchKind.StrideTouch, quality: 0.9f));

        Assert.True(good > poor,
            $"quality 0.9 put the ball {good:F2} m/s ahead but quality 0.3 managed {poor:F2}: " +
            "the first touch is not the floor for the planned touch");
    }

    [Fact]
    public void NoKind_TakesTheBallPastItsOwnAllowance()
    {
        // A hard pass and a sprinting receiver: the neutral cap is already wide, so this is the case
        // where an unbounded push would show up.
        Vector2 fastIncoming = new(-25f, 0f);
        Vector2 sprint = new(8f, 0f);
        float quality = 0.5f;
        float cap = sprint.Length() + 1.2f + (1f - quality) * fastIncoming.Length() * 0.15f;
        Vector2 neutral = sprint * 0.95f + TouchDirection * 0.9f + fastIncoming * ((1f - quality) * 0.30f);

        foreach (ReceptionTouchKind kind in Enum.GetValues<ReceptionTouchKind>())
        {
            ReceptionTouchModel.TouchShape shape = ReceptionTouchModel.ShapeOf(kind);
            Vector2 resolved = ReceptionTouchModel.Resolve(
                new ReceptionIntent(kind), neutral, sprint, fastIncoming, quality, TouchDirection, cap);

            Assert.True(resolved.Length() <= cap + shape.CapBonus + 1e-3f,
                $"{kind} produced {resolved.Length():F2} m/s against a cap of {cap + shape.CapBonus:F2}");
            Assert.True(resolved.Length() <= MathF.Max(cap, sprint.Length() + ReceptionTouchModel.MaxRelativeTouchSpeed) + 1e-3f,
                $"{kind} launched the ball past the dribble spring's own bound");
            Assert.True(float.IsFinite(resolved.X) && float.IsFinite(resolved.Y), $"{kind} produced a non-finite velocity");
        }
    }

    [Fact]
    public void AZeroTouchDirection_IsSurvivable()
    {
        float quality = 0.8f;
        Vector2 resolved = ReceptionTouchModel.Resolve(
            new ReceptionIntent(ReceptionTouchKind.StrideTouch), NeutralCushion(quality), Vector2.Zero,
            Incoming, quality, Vector2.Zero, NeutralCap(quality));

        Assert.True(float.IsFinite(resolved.X) && float.IsFinite(resolved.Y),
            "a zero facing produced a non-finite touch instead of just the retained pace");
    }
}
