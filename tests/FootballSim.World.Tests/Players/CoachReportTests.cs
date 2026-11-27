using System;
using System.Collections.Generic;
using System.Reflection;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Players;

/// <summary>
/// W1 coach-report and Authentic-mode fog-of-war contract (plan §4.6): the report stays qualitative (three
/// to five readable sentences, no digits, no ceiling/plasticity vocabulary), its estimates get more accurate
/// as judging ability rises, and the public view type graph is structurally unable to expose hidden
/// potential while still showing all eighteen current attributes grouped by category.
/// </summary>
public sealed class CoachReportTests
{
    private static readonly AttributeCategory[] Categories =
    {
        AttributeCategory.Innate,
        AttributeCategory.BodyTrainable,
        AttributeCategory.SkillAndMind
    };

    private static readonly string[] ForbiddenNameFragments = { "Ceiling", "Plasticity", "Latent" };

    private static readonly string[] ArrowSymbols = { "^", "-", "v" };

    // =============================================================================================
    // Report shape and wording.
    // =============================================================================================

    [Fact]
    public void Generate_ProducesThreeToFiveSentences_JoinedIntoTheText()
    {
        WorldPlayer player = MakePlayer(50f, 70f);
        float[] judgingAbilities = { 0f, 0.25f, 0.5f, 0.75f, 1f };

        for (int seed = 1; seed <= 15; seed++)
        {
            foreach (float judgingAbility in judgingAbilities)
            {
                CoachReport report = CoachReportGenerator.Generate(
                    player, judgingAbility, new WorldRng((ulong)seed));

                Assert.InRange(
                    report.Sentences.Count,
                    CoachReportGenerator.MinimumSentences,
                    CoachReportGenerator.MaximumSentences);
                Assert.NotEmpty(report.Summary);

                foreach (string sentence in report.Sentences)
                {
                    Assert.False(string.IsNullOrWhiteSpace(sentence), $"seed {seed}: empty sentence.");
                    Assert.Equal(sentence, sentence.Trim());
                }

                Assert.Equal(string.Join(" ", report.Sentences), report.Text);
            }
        }
    }

    [Fact]
    public void ReportText_NeverContainsDigitsOrHiddenPotentialVocabulary()
    {
        WorldPlayer young = MakePlayer(45f, 75f);
        WorldPlayer capped = MakePlayer(80f, 80f);

        for (int seed = 1; seed <= 30; seed++)
        {
            float judgingAbility = (seed % 11) / 10f;
            foreach (WorldPlayer player in new[] { young, capped })
            {
                CoachReport report = CoachReportGenerator.Generate(
                    player, judgingAbility, new WorldRng((ulong)seed));
                AssertNoDigitsOrForbiddenWords(report.Text);
                AssertNoDigitsOrForbiddenWords(report.Summary);
            }
        }
    }

    [Fact]
    public void Reports_VaryBetweenSeeds()
    {
        WorldPlayer player = MakePlayer(50f, 70f);
        var texts = new HashSet<string>(StringComparer.Ordinal);
        for (int seed = 1; seed <= 25; seed++)
        {
            texts.Add(CoachReportGenerator.Generate(player, 0.5f, new WorldRng((ulong)seed)).Text);
        }

        Assert.True(texts.Count >= 3, $"expected varied reports, got {texts.Count} distinct texts.");
    }

    // =============================================================================================
    // Determinism.
    // =============================================================================================

    [Fact]
    public void Generate_IsDeterministicForTheSameSeed()
    {
        WorldPlayer player = MakePlayer(52f, 68f);
        for (int seed = 1; seed <= 10; seed++)
        {
            CoachReport first = CoachReportGenerator.Generate(player, 0.4f, new WorldRng((ulong)seed));
            CoachReport second = CoachReportGenerator.Generate(player, 0.4f, new WorldRng((ulong)seed));
            AssertSameReport(first, second);
        }
    }

    // =============================================================================================
    // Accuracy model.
    // =============================================================================================

    [Fact]
    public void ObservationNoise_MapsAmateurToNinePoints_AndEliteToTwoAndAHalf()
    {
        WorldPlayer player = MakePlayer(50f, 70f);
        Assert.Equal(9f, CoachReportGenerator.Generate(player, 0f, new WorldRng(1)).ObservationNoise, 4);
        Assert.Equal(5.75f, CoachReportGenerator.Generate(player, 0.5f, new WorldRng(1)).ObservationNoise, 4);
        Assert.Equal(2.5f, CoachReportGenerator.Generate(player, 1f, new WorldRng(1)).ObservationNoise, 4);
    }

    [Fact]
    public void HigherJudgingAbility_ProducesGapEstimatesCloserToTheTrueGap()
    {
        const int SeedCount = 200;
        WorldPlayer player = MakePlayer(50f, 75f); // a true remaining gap of 25 points in every category
        foreach (AttributeCategory category in Categories)
        {
            Assert.Equal(25f, player.Attributes.MeanGap(category), 3);
        }

        double amateurError = 0d;
        double eliteError = 0d;
        for (int seed = 1; seed <= SeedCount; seed++)
        {
            CoachReport amateur = CoachReportGenerator.Generate(player, 0f, new WorldRng((ulong)seed));
            CoachReport elite = CoachReportGenerator.Generate(player, 1f, new WorldRng((ulong)seed));

            foreach (AttributeCategory category in Categories)
            {
                float truth = player.Attributes.MeanGap(category);
                amateurError += Math.Abs(Estimate(amateur, category) - truth);
                eliteError += Math.Abs(Estimate(elite, category) - truth);
            }
        }

        double amateurMae = amateurError / (SeedCount * Categories.Length);
        double eliteMae = eliteError / (SeedCount * Categories.Length);

        // ±9 points of noise versus ±2.5: the elite judge's mean absolute error must be far smaller.
        Assert.True(
            amateurMae > 4.0,
            $"an amateur judge should be far off (mean absolute error {amateurMae:0.00});");
        Assert.True(
            eliteMae < 4.0,
            $"an elite judge should be close (mean absolute error {eliteMae:0.00});");
        Assert.True(
            amateurMae > eliteMae + 2.5,
            $"expected the amateur error ({amateurMae:0.00}) to beat the elite error ({eliteMae:0.00}) by a clear margin.");
    }

    [Fact]
    public void Generate_ClampsEveryEstimateToTheDocumentedRange()
    {
        WorldPlayer capped = MakePlayer(80f, 80f); // no remaining room anywhere
        for (int seed = 1; seed <= 20; seed++)
        {
            CoachReport report = CoachReportGenerator.Generate(capped, 0f, new WorldRng((ulong)seed));
            Assert.InRange(report.InnateGapEstimate, -20f, 100f);
            Assert.InRange(report.BodyGapEstimate, -20f, 100f);
            Assert.InRange(report.SkillGapEstimate, -20f, 100f);
            AssertNoDigitsOrForbiddenWords(report.Text);
        }
    }

    // =============================================================================================
    // Validation.
    // =============================================================================================

    [Fact]
    public void Generate_ValidatesItsArguments()
    {
        WorldPlayer player = MakePlayer(50f, 70f);

        Assert.Throws<ArgumentNullException>(
            () => CoachReportGenerator.Generate(null!, 0.5f, new WorldRng(1)));
        Assert.Throws<ArgumentNullException>(
            () => CoachReportGenerator.Generate(player, 0.5f, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoachReportGenerator.Generate(player, -0.01f, new WorldRng(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoachReportGenerator.Generate(player, 1.01f, new WorldRng(1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CoachReportGenerator.Generate(player, float.NaN, new WorldRng(1)));
    }

    // =============================================================================================
    // Authentic-mode public view.
    // =============================================================================================

    [Fact]
    public void PublicView_HasThreeGroupsCoveringAllEighteenAttributes()
    {
        WorldPlayer player = MakePlayer(50f, 72f);
        PlayerPublicView view = PlayerPublicViewBuilder.Build(player, 0.6f, new WorldRng(42));

        Assert.Equal(3, view.AttributeGroups.Count);

        (AttributeCategory Category, string Label)[] expectedGroups =
        {
            (AttributeCategory.Innate, "Innate"),
            (AttributeCategory.BodyTrainable, "Body-Trainable"),
            (AttributeCategory.SkillAndMind, "Skill & Mind")
        };

        var seen = new HashSet<AttributeId>();
        int total = 0;
        for (int groupIndex = 0; groupIndex < expectedGroups.Length; groupIndex++)
        {
            PublicAttributeGroup group = view.AttributeGroups[groupIndex];
            Assert.Equal(expectedGroups[groupIndex].Category, group.Category);
            Assert.Equal(expectedGroups[groupIndex].Label, group.Label);

            IReadOnlyList<AttributeId> expectedIds = AttributeMetadata.OfCategory(group.Category);
            Assert.Equal(expectedIds.Count, group.Attributes.Count);
            for (int i = 0; i < expectedIds.Count; i++)
            {
                PublicAttributeView attribute = group.Attributes[i];
                Assert.Equal(expectedIds[i], attribute.Id);
                Assert.Equal(group.Category, attribute.Category);
                Assert.Equal(AttributeMetadata.Get(expectedIds[i]).Label, attribute.Label);
                Assert.InRange(attribute.Current, 1, 99);
                Assert.True(seen.Add(attribute.Id), $"{attribute.Id} appears in more than one group.");
                total++;
            }
        }

        Assert.Equal(3 + 4 + 11, total);
        Assert.Equal(AttributeMetadata.Count, seen.Count);

        // Identity and player state come straight from the evaluator/entity.
        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);
        Assert.Equal(player.Id, view.Id);
        Assert.Equal(player.DisplayName, view.DisplayName);
        Assert.Equal(player.Age, view.Age);
        Assert.Equal(player.PreferredFoot, view.PreferredFoot);
        Assert.Equal(identity.PrimaryRole, view.PrimaryRole);
        Assert.Equal(identity.SecondaryRoles.Count, view.SecondaryRoles.Count);
        Assert.Equal(identity.RoleSuitabilities.Count, view.RoleSuitabilities.Count);
        Assert.Equal(player.Condition01, view.Condition01);
        Assert.Equal(player.Form01, view.Form01);
        Assert.Equal(player.Visibility01, view.Visibility01);
    }

    [Fact]
    public void PublicView_ArrowsFollowTheTwelveWeekTrend_AndCurrentsAreRoundedIntegers()
    {
        WorldPlayer player = MakePlayer(50f, 70f);
        player.Trends.Record(player.Attributes); // snapshot: everything at fifty
        player.Attributes.SetCurrent(AttributeId.TopSpeed, 60f);
        player.Attributes.SetCurrent(AttributeId.Acceleration, 40f);
        player.Attributes.SetCurrent(AttributeId.Passing, 50.2f);
        player.Attributes.SetCurrent(AttributeId.Strength, 57.6f);

        PlayerPublicView view = PlayerPublicViewBuilder.Build(player, 0.5f, new WorldRng(7));
        Dictionary<AttributeId, PublicAttributeView> rows = Flatten(view);

        Assert.Equal("^", rows[AttributeId.TopSpeed].TrendArrow);
        Assert.Equal(10f, rows[AttributeId.TopSpeed].Trend, 3);
        Assert.Equal(60, rows[AttributeId.TopSpeed].Current);

        Assert.Equal("v", rows[AttributeId.Acceleration].TrendArrow);
        Assert.Equal(-10f, rows[AttributeId.Acceleration].Trend, 3);
        Assert.Equal(40, rows[AttributeId.Acceleration].Current);

        Assert.Equal("-", rows[AttributeId.Passing].TrendArrow);
        Assert.Equal(0.2f, rows[AttributeId.Passing].Trend, 3);

        Assert.Equal("^", rows[AttributeId.Strength].TrendArrow);
        Assert.Equal(58, rows[AttributeId.Strength].Current);

        foreach (PublicAttributeGroup group in view.AttributeGroups)
        {
            foreach (PublicAttributeView attribute in group.Attributes)
            {
                Assert.Equal(
                    player.Trends.Delta(attribute.Id, PlayerTrendHistory.WindowWeeks, player.Attributes),
                    attribute.Trend,
                    4);
                Assert.Contains(attribute.TrendArrow, ArrowSymbols);
                Assert.Equal(
                    (int)MathF.Round(player.Attributes.Current(attribute.Id), MidpointRounding.AwayFromZero),
                    attribute.Current);
            }
        }
    }

    [Fact]
    public void PublicViewTypeGraph_HasNoCeilingPlasticityOrLatentMember()
    {
        AssertNoHiddenPotentialMember(typeof(PublicAttributeView), new HashSet<Type>());
        AssertNoHiddenPotentialMember(typeof(PublicAttributeGroup), new HashSet<Type>());
        AssertNoHiddenPotentialMember(typeof(PlayerPublicView), new HashSet<Type>());
    }

    [Fact]
    public void PublicViewBuild_IsDeterministic()
    {
        WorldPlayer player = MakePlayer(48f, 66f);
        PlayerPublicView first = PlayerPublicViewBuilder.Build(player, 0.65f, new WorldRng(2024));
        PlayerPublicView second = PlayerPublicViewBuilder.Build(player, 0.65f, new WorldRng(2024));

        AssertSameView(first, second);

        // The builder must use the same generator, so an identical stream yields the identical report.
        CoachReport direct = CoachReportGenerator.Generate(player, 0.65f, new WorldRng(2024));
        AssertSameReport(direct, first.CoachReport);
    }

    [Fact]
    public void PublicViewBuilder_ValidatesItsArguments()
    {
        WorldPlayer player = MakePlayer(50f, 70f);
        Assert.Throws<ArgumentNullException>(
            () => PlayerPublicViewBuilder.Build(null!, 0.5f, new WorldRng(1)));
        Assert.Throws<ArgumentNullException>(
            () => PlayerPublicViewBuilder.Build(player, 0.5f, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerPublicViewBuilder.Build(player, 2f, new WorldRng(1)));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>
    /// Generates a deterministic player and then forces every attribute to the same current/ceiling pair, so
    /// the true remaining gap of every category is exactly <paramref name="ceiling"/> minus
    /// <paramref name="current"/>.
    /// </summary>
    private static WorldPlayer MakePlayer(float current, float ceiling)
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(4242UL, 18, BackstoryKind.LocalClubYouth);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCeiling(id, ceiling);
            player.Attributes.SetCurrent(id, current);
        }

        return player;
    }

    private static float Estimate(CoachReport report, AttributeCategory category) => category switch
    {
        AttributeCategory.Innate => report.InnateGapEstimate,
        AttributeCategory.BodyTrainable => report.BodyGapEstimate,
        _ => report.SkillGapEstimate
    };

    private static void AssertNoDigitsOrForbiddenWords(string text)
    {
        foreach (char character in text)
        {
            Assert.False(char.IsDigit(character), $"Report text contains a digit: \"{text}\"");
        }

        Assert.False(
            text.Contains("ceiling", StringComparison.OrdinalIgnoreCase),
            $"Report text leaks a ceiling: \"{text}\"");
        Assert.False(
            text.Contains("plasticity", StringComparison.OrdinalIgnoreCase),
            $"Report text leaks a plasticity: \"{text}\"");
    }

    private static void AssertSameReport(CoachReport expected, CoachReport actual)
    {
        Assert.Equal(expected.Sentences.Count, actual.Sentences.Count);
        for (int i = 0; i < expected.Sentences.Count; i++)
        {
            Assert.Equal(expected.Sentences[i], actual.Sentences[i]);
        }

        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.InnateGapEstimate, actual.InnateGapEstimate);
        Assert.Equal(expected.BodyGapEstimate, actual.BodyGapEstimate);
        Assert.Equal(expected.SkillGapEstimate, actual.SkillGapEstimate);
        Assert.Equal(expected.ObservationNoise, actual.ObservationNoise);
        Assert.Equal(expected.Text, actual.Text);
    }

    private static void AssertSameView(PlayerPublicView expected, PlayerPublicView actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Age, actual.Age);
        Assert.Equal(expected.PreferredFoot, actual.PreferredFoot);
        Assert.Equal(expected.PrimaryRole, actual.PrimaryRole);
        Assert.Equal(expected.Condition01, actual.Condition01);
        Assert.Equal(expected.Form01, actual.Form01);
        Assert.Equal(expected.Visibility01, actual.Visibility01);

        Assert.Equal(expected.SecondaryRoles.Count, actual.SecondaryRoles.Count);
        for (int i = 0; i < expected.SecondaryRoles.Count; i++)
        {
            Assert.Equal(expected.SecondaryRoles[i], actual.SecondaryRoles[i]);
        }

        Assert.Equal(expected.StyleTags.Count, actual.StyleTags.Count);
        for (int i = 0; i < expected.StyleTags.Count; i++)
        {
            Assert.Equal(expected.StyleTags[i], actual.StyleTags[i]);
        }

        Assert.Equal(expected.RoleSuitabilities.Count, actual.RoleSuitabilities.Count);
        for (int i = 0; i < expected.RoleSuitabilities.Count; i++)
        {
            Assert.Equal(expected.RoleSuitabilities[i], actual.RoleSuitabilities[i]);
        }

        Assert.Equal(expected.AttributeGroups.Count, actual.AttributeGroups.Count);
        for (int groupIndex = 0; groupIndex < expected.AttributeGroups.Count; groupIndex++)
        {
            PublicAttributeGroup expectedGroup = expected.AttributeGroups[groupIndex];
            PublicAttributeGroup actualGroup = actual.AttributeGroups[groupIndex];
            Assert.Equal(expectedGroup.Category, actualGroup.Category);
            Assert.Equal(expectedGroup.Label, actualGroup.Label);
            Assert.Equal(expectedGroup.Attributes.Count, actualGroup.Attributes.Count);
            for (int i = 0; i < expectedGroup.Attributes.Count; i++)
            {
                Assert.Equal(expectedGroup.Attributes[i], actualGroup.Attributes[i]);
            }
        }

        AssertSameReport(expected.CoachReport, actual.CoachReport);
    }

    private static Dictionary<AttributeId, PublicAttributeView> Flatten(PlayerPublicView view)
    {
        var rows = new Dictionary<AttributeId, PublicAttributeView>(AttributeMetadata.Count);
        foreach (PublicAttributeGroup group in view.AttributeGroups)
        {
            foreach (PublicAttributeView attribute in group.Attributes) rows.Add(attribute.Id, attribute);
        }

        return rows;
    }

    private static void AssertNoHiddenPotentialMember(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type)) return;

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (string fragment in ForbiddenNameFragments)
            {
                Assert.False(
                    property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"{type.Name}.{property.Name} would expose hidden potential.");
            }

            CollectElementTypes(property.PropertyType, visited);
        }
    }

    private static void CollectElementTypes(Type propertyType, HashSet<Type> visited)
    {
        if (propertyType.IsArray)
        {
            Type? element = propertyType.GetElementType();
            if (element is not null && IsWorldType(element)) AssertNoHiddenPotentialMember(element, visited);
            return;
        }

        if (propertyType.IsGenericType)
        {
            foreach (Type argument in propertyType.GetGenericArguments())
            {
                if (IsWorldType(argument)) AssertNoHiddenPotentialMember(argument, visited);
            }

            return;
        }

        if (IsWorldType(propertyType)) AssertNoHiddenPotentialMember(propertyType, visited);
    }

    private static bool IsWorldType(Type type) =>
        !type.IsPrimitive &&
        !type.IsEnum &&
        type != typeof(string) &&
        type != typeof(object) &&
        type.Namespace is not null &&
        type.Namespace.StartsWith("FootballSim", StringComparison.Ordinal);
}
