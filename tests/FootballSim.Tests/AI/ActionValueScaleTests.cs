using System;
using FootballSim.AI;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A4.4 — one documented scale for pass, carry and shot.
///
/// The scale's own claims are what these tests pin: the loss term is the single owner of transition risk
/// and grows toward your own goal; a safe recycle can beat a forced forward pass; a clearly better chance
/// beats a poor shot; and every composed value is finite and expressed in the same unit. The behavioural
/// half of A4.4 (no blanket backward-pass ban, the shot's flat curve, keeper support off his line) is
/// covered by <see cref="OnBallActionSetTests"/>.
/// </summary>
public sealed class ActionValueScaleTests
{
    /// <summary>Losing the ball in your own third costs more than losing it in the opponent's half.</summary>
    [Fact]
    public void LosingTheBallDeep_CostsMore()
    {
        float atOwnGoal = ActionValueScale.LossCost(-50f);
        float halfway = ActionValueScale.LossCost(0f);
        float atTheirGoal = ActionValueScale.LossCost(50f);

        Assert.True(atOwnGoal > halfway);
        Assert.Equal(ActionValueScale.LossBase, halfway, precision: 4);
        Assert.Equal(halfway, atTheirGoal, precision: 4);
        Assert.True(atOwnGoal <= ActionValueScale.LossBase + ActionValueScale.LossOwnThird + 1e-4f);
    }

    /// <summary>
    /// The plan's A4.4 condition, stated as a test: <i>safe recycling can beat a dangerous forced pass</i>.
    /// A high-probability ball back to a free defender is worth more than a 35 % ball into a crowd.
    /// </summary>
    [Fact]
    public void ASafeRecycle_BeatsAForcedForwardPass()
    {
        // A backward ball to a free team-mate, and a forced ball into a contested forward area.
        float recycle = ActionValueScale.ExpectedValue(0.95f, gain: 0.85f, loss: ActionValueScale.LossCost(-20f));
        float forced = ActionValueScale.ExpectedValue(0.35f, gain: 1.10f, loss: ActionValueScale.LossCost(-20f));

        Assert.True(recycle > forced + 0.2f, $"recycle {recycle:F2} vs forced {forced:F2}");
    }

    /// <summary>
    /// And the other A4.4 condition: <i>a better chance can beat a poor shot</i>. A shot's value is a
    /// documented curve with a lane term; a clear chance is worth more than a blocked one, and the value is
    /// comparable to a pass's.
    /// </summary>
    [Fact]
    public void ABetterChance_BeatsAPoorShot()
    {
        float clearInTheBox = ActionValueScale.ShotValue(10f, laneClearance01: 0.9f);
        float blockedFromRange = ActionValueScale.ShotValue(22f, laneClearance01: 0.2f);
        float goodPass = ActionValueScale.ExpectedValue(0.9f, gain: 1.2f, loss: ActionValueScale.LossCost(0f));

        Assert.True(clearInTheBox > blockedFromRange + 0.3f);
        Assert.InRange(clearInTheBox, 0.5f, 2.5f);
        // A clear chance is worth about as much as a good pass — not twice as much, and not a tenth.
        Assert.True(MathF.Abs(clearInTheBox - goodPass) < 0.8f,
            $"shot {clearInTheBox:F2} vs pass {goodPass:F2} are not on a comparable scale");
    }

    /// <summary>The common shape is implemented once: no reach ⇒ you pay the loss, full reach ⇒ the gain.</summary>
    [Fact]
    public void TheExpectedValueShape_IsTheDocumentedConvention()
    {
        Assert.Equal(-1.5f, ActionValueScale.ExpectedValue(0f, gain: 2f, loss: 1.5f), precision: 4);
        Assert.Equal(2f, ActionValueScale.ExpectedValue(1f, gain: 2f, loss: 1.5f), precision: 4);
        Assert.Equal(0.25f, ActionValueScale.ExpectedValue(0.5f, gain: 2f, loss: 1.5f), precision: 4);
    }

    /// <summary>Every public constant the scale exposes is a usable number, not NaN or a runaway.</summary>
    [Fact]
    public void TheScale_IsFiniteEverywhere()
    {
        Assert.True(float.IsFinite(ActionValueScale.Unit) && ActionValueScale.Unit > 0f);
        Assert.InRange(ActionValueScale.CarryBaseValue, 0f, 2f);
        Assert.InRange(ActionValueScale.CarrySpaceBonus, 0f, 2f);
        Assert.InRange(ActionValueScale.ShotBaseValue, 0f, 2f);
        Assert.InRange(ActionValueScale.ShotLaneBonus, 0f, 2f);
        Assert.InRange(ActionValueScale.ProgressBonus, 0f, 2f);

        foreach (float distance in new[] { 1f, 6f, 16f, 23f, 40f })
        foreach (float lane in new[] { 0f, 0.5f, 1f })
        {
            float value = ActionValueScale.ShotValue(distance, lane);
            Assert.True(float.IsFinite(value));
            Assert.InRange(value, 0f, 4f);
        }
    }
}
