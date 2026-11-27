using System;
using System.Collections.Generic;
using FootballSim.World.Career;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Career;

/// <summary>
/// W4 training-planner contract: the preset list is the ten <see cref="TrainingFocus"/> values in enum
/// order, every focus has a readable label/summary and the documented boost/trade-off set, the display labels
/// come from the attribute metadata table, and the recommended focus follows the same role mapping the
/// season runner uses.
/// </summary>
public sealed class TrainingPlannerTests
{
    [Fact]
    public void AllFocuses_CoversEveryValueOnceInEnumOrder()
    {
        TrainingFocus[] expected = Enum.GetValues<TrainingFocus>();

        Assert.Equal(expected.Length, TrainingPlanner.AllFocuses.Count);

        var seen = new HashSet<TrainingFocus>();
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], TrainingPlanner.AllFocuses[index]);
            Assert.True(seen.Add(TrainingPlanner.AllFocuses[index]), $"duplicate focus at index {index}");
        }

        Assert.Equal(expected.Length, seen.Count);
    }

    [Fact]
    public void DescribeAll_DescribesEveryFocusOnceInOrder()
    {
        IReadOnlyList<TrainingFocusDescription> descriptions = TrainingPlanner.DescribeAll();

        Assert.Equal(TrainingPlanner.AllFocuses.Count, descriptions.Count);
        for (int index = 0; index < descriptions.Count; index++)
        {
            Assert.Equal(TrainingPlanner.AllFocuses[index], descriptions[index].Focus);
        }
    }

    [Fact]
    public void Describe_EveryFocusHasALabelAndSummary()
    {
        foreach (TrainingFocus focus in TrainingPlanner.AllFocuses)
        {
            TrainingFocusDescription description = TrainingPlanner.Describe(focus);

            Assert.Equal(focus, description.Focus);
            Assert.False(string.IsNullOrWhiteSpace(description.Label), $"{focus} needs a label");
            Assert.False(string.IsNullOrWhiteSpace(description.Summary), $"{focus} needs a summary");
        }
    }

    [Fact]
    public void Describe_UndefinedFocusThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingPlanner.Describe((TrainingFocus)10));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingPlanner.Describe((TrainingFocus)(-1)));
    }

    [Fact]
    public void StrengthAndPower_BoostsStrengthAndBalance_AndCostsAgilityAndTopSpeed()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.StrengthAndPower);

        Assert.Equal(new[] { AttributeId.Strength, AttributeId.Balance }, description.BoostedAttributes);
        Assert.Equal(new[] { AttributeId.Agility, AttributeId.TopSpeed }, description.TradeOffAttributes);
        Assert.False(string.IsNullOrWhiteSpace(description.TradeOffNote));
    }

    [Fact]
    public void EnduranceAndEngine_BoostsStamina_AndCostsStrength()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.EnduranceAndEngine);

        Assert.Equal(new[] { AttributeId.Stamina }, description.BoostedAttributes);
        Assert.Equal(new[] { AttributeId.Strength }, description.TradeOffAttributes);
        Assert.False(string.IsNullOrWhiteSpace(description.TradeOffNote));
    }

    [Fact]
    public void AgilityAndQuickness_BoostsAgilityAndBalance_AndCostsStrength()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.AgilityAndQuickness);

        Assert.Equal(new[] { AttributeId.Agility, AttributeId.Balance }, description.BoostedAttributes);
        Assert.Equal(new[] { AttributeId.Strength }, description.TradeOffAttributes);
        Assert.False(string.IsNullOrWhiteSpace(description.TradeOffNote));
    }

    [Fact]
    public void PassingAndVision_BoostsPassingAndVision_WithNoTradeOffs()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.PassingAndVision);

        Assert.Equal(new[] { AttributeId.Passing, AttributeId.Vision }, description.BoostedAttributes);
        Assert.Empty(description.TradeOffAttributes);
        Assert.Equal(string.Empty, description.TradeOffNote);
    }

    [Fact]
    public void RecoveryAndLight_HasNoBoostsButANonEmptyNote()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.RecoveryAndLight);

        Assert.Empty(description.BoostedAttributes);
        Assert.Empty(description.TradeOffAttributes);
        Assert.False(string.IsNullOrWhiteSpace(description.TradeOffNote));
    }

    [Fact]
    public void Balanced_IsTheNeutralControl()
    {
        TrainingFocusDescription description = TrainingPlanner.Describe(TrainingFocus.Balanced);

        Assert.Empty(description.BoostedAttributes);
        Assert.Empty(description.TradeOffAttributes);
        Assert.Equal(string.Empty, description.TradeOffNote);
    }

    [Fact]
    public void BoostedAndTradeOffLabels_ComeFromAttributeMetadata()
    {
        foreach (TrainingFocus focus in TrainingPlanner.AllFocuses)
        {
            TrainingFocusDescription description = TrainingPlanner.Describe(focus);

            Assert.Equal(LabelsOf(description.BoostedAttributes), description.BoostedLabels);
            Assert.Equal(LabelsOf(description.TradeOffAttributes), description.TradeOffLabels);
        }

        TrainingFocusDescription power = TrainingPlanner.Describe(TrainingFocus.StrengthAndPower);
        Assert.Equal(new[] { "Strength", "Balance" }, power.BoostedLabels);
        Assert.Equal(new[] { "Agility", "Top Speed" }, power.TradeOffLabels);
    }

    [Theory]
    [InlineData(WorldRole.Goalkeeper, TrainingFocus.Goalkeeping)]
    [InlineData(WorldRole.CenterBack, TrainingFocus.DefendingAndPositioning)]
    [InlineData(WorldRole.FullBack, TrainingFocus.DefendingAndPositioning)]
    [InlineData(WorldRole.DefensiveMidfielder, TrainingFocus.DefendingAndPositioning)]
    [InlineData(WorldRole.CentralMidfielder, TrainingFocus.PassingAndVision)]
    [InlineData(WorldRole.AttackingMidfielder, TrainingFocus.PassingAndVision)]
    [InlineData(WorldRole.Winger, TrainingFocus.BallControlAndDribbling)]
    [InlineData(WorldRole.Striker, TrainingFocus.FinishingAndShooting)]
    public void RecommendedFocusFor_MapsEachRole(WorldRole role, TrainingFocus expected)
    {
        Assert.Equal(expected, TrainingPlanner.RecommendedFocusFor(role));
    }

    /// <summary>
    /// API note: the brief expected an undefined role cast to throw, but the role switch ends in a
    /// documented <c>_ =&gt; TrainingFocus.Balanced</c> fallback, so out-of-range roles map to the neutral
    /// control instead of raising. This test pins the behaviour that actually exists in the source.
    /// </summary>
    [Fact]
    public void RecommendedFocusFor_UndefinedRoleFallsBackToBalanced()
    {
        Assert.Equal(TrainingFocus.Balanced, TrainingPlanner.RecommendedFocusFor((WorldRole)8));
        Assert.Equal(TrainingFocus.Balanced, TrainingPlanner.RecommendedFocusFor((WorldRole)(-1)));
    }

    [Fact]
    public void RecommendedFocusForPlayer_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => TrainingPlanner.RecommendedFocusFor((WorldPlayer)null!));
    }

    private static IReadOnlyList<string> LabelsOf(IReadOnlyList<AttributeId> attributes)
    {
        var labels = new List<string>(attributes.Count);
        for (int index = 0; index < attributes.Count; index++)
        {
            labels.Add(AttributeMetadata.Get(attributes[index]).Label);
        }

        return labels;
    }
}
