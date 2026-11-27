using System;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Players;

/// <summary>
/// W1 growth-engine contract (plan §4.5): the age-curve and soft-ceiling tables, category dynamics, body
/// trade-offs, match-minute effects, soft ceiling nudges, permanent injury damage, the result bookkeeping,
/// determinism and input validation. Scenarios are built from <see cref="PlayerGenerator.GenerateAuthentic"/>
/// plus direct attribute edits, so every assertion is reproducible and the calibrated constants stay pinned.
/// </summary>
public sealed class GrowthEngineTests
{
    private const int AttributeCount = 18;
    private const int CareerWeeks = 208; // Four 52-week seasons.

    /// <summary>Every injury kind with the exact set of ceilings it is allowed to shave and in which range.</summary>
    private static readonly (InjuryKind Kind, (AttributeId Id, float Min, float Max)[] Shaves)[] PermanentDamageTable =
    {
        (InjuryKind.KneeLigament, new[]
        {
            (AttributeId.TopSpeed, 3.0f, 6.0f),
            (AttributeId.Acceleration, 2.5f, 5.0f),
            (AttributeId.Agility, 2.0f, 4.0f)
        }),
        (InjuryKind.HamstringTear, new[]
        {
            (AttributeId.TopSpeed, 1.5f, 3.5f),
            (AttributeId.Acceleration, 1.5f, 3.0f)
        }),
        (InjuryKind.AnkleLigament, new[]
        {
            (AttributeId.Agility, 1.5f, 3.5f),
            (AttributeId.Balance, 1.0f, 2.5f)
        }),
        (InjuryKind.FootFracture, new[]
        {
            (AttributeId.Acceleration, 1.5f, 3.0f),
            (AttributeId.Agility, 1.0f, 2.5f)
        }),
        (InjuryKind.BackInjury, new[]
        {
            (AttributeId.Balance, 1.0f, 2.5f),
            (AttributeId.Strength, 1.0f, 2.0f)
        }),
        (InjuryKind.Concussion, Array.Empty<(AttributeId, float, float)>()),
        (InjuryKind.Illness, Array.Empty<(AttributeId, float, float)>()),
        (InjuryKind.GroinStrain, Array.Empty<(AttributeId, float, float)>()),
        (InjuryKind.CalfStrain, Array.Empty<(AttributeId, float, float)>()),
        (InjuryKind.None, Array.Empty<(AttributeId, float, float)>())
    };

    // =============================================================================================
    // 1. Training environment presets.
    // =============================================================================================

    [Fact]
    public void TrainingEnvironment_ForTier_ReturnsThePlanDefaults()
    {
        Assert.Equal(new TrainingEnvironment(1, 1.00f, 5), TrainingEnvironment.ForTier(1));
        Assert.Equal(new TrainingEnvironment(2, 1.00f, 5), TrainingEnvironment.ForTier(2));
        Assert.Equal(new TrainingEnvironment(3, 1.00f, 4), TrainingEnvironment.ForTier(3));
        Assert.Equal(new TrainingEnvironment(4, 1.00f, 3), TrainingEnvironment.ForTier(4));
        Assert.Equal(new TrainingEnvironment(5, 1.00f, 2), TrainingEnvironment.ForTier(5));
        Assert.Equal(new TrainingEnvironment(6, 1.00f, 1), TrainingEnvironment.ForTier(6));

        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingEnvironment.ForTier(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingEnvironment.ForTier(7));
    }

    [Fact]
    public void TrainingEnvironment_EveryTierPreset_CanAdvanceAWeek()
    {
        // The amateur-tier presets deliberately encode coaching worse than the 0.70 club floor; they are the
        // sanctioned defaults, so the engine must accept every one of them.
        for (int tier = 1; tier <= 6; tier++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                400UL + (ulong)tier, 20, BackstoryKind.LocalClubYouth);
            GrowthEngine.AdvanceWeek(
                player, TrainingEnvironment.ForTier(tier), TrainingFocus.Balanced, 0, 0f, GrowthRng(900UL));
        }
    }

    // =============================================================================================
    // 2. Age-curve and soft-ceiling tables.
    // =============================================================================================

    [Fact]
    public void AgeCurveMultiplier_MatchesPlanBands_AtEveryBoundary()
    {
        (int Age, AttributeCategory Category, float Expected)[] cases =
        {
            // Innate: under 16 and 16..20 use 0.25; 21..27 and 28+ use 0.00 (decline is separate).
            (14, AttributeCategory.Innate, 0.25f),
            (16, AttributeCategory.Innate, 0.25f),
            (20, AttributeCategory.Innate, 0.25f),
            (21, AttributeCategory.Innate, 0.00f),
            (27, AttributeCategory.Innate, 0.00f),
            (28, AttributeCategory.Innate, 0.00f),
            (35, AttributeCategory.Innate, 0.00f),

            // Body-trainable: 16..22 use 1.15; 23..26 use 0.65; 27+ use 0.00.
            (14, AttributeCategory.BodyTrainable, 1.15f),
            (16, AttributeCategory.BodyTrainable, 1.15f),
            (22, AttributeCategory.BodyTrainable, 1.15f),
            (23, AttributeCategory.BodyTrainable, 0.65f),
            (26, AttributeCategory.BodyTrainable, 0.65f),
            (27, AttributeCategory.BodyTrainable, 0.00f),
            (30, AttributeCategory.BodyTrainable, 0.00f),

            // Skill-and-mind: 16..23 use 1.20; 24..28 use 0.75; 29..33 use 0.30; 34+ use 0.00.
            (14, AttributeCategory.SkillAndMind, 1.20f),
            (16, AttributeCategory.SkillAndMind, 1.20f),
            (23, AttributeCategory.SkillAndMind, 1.20f),
            (24, AttributeCategory.SkillAndMind, 0.75f),
            (28, AttributeCategory.SkillAndMind, 0.75f),
            (29, AttributeCategory.SkillAndMind, 0.30f),
            (33, AttributeCategory.SkillAndMind, 0.30f),
            (34, AttributeCategory.SkillAndMind, 0.00f),
            (40, AttributeCategory.SkillAndMind, 0.00f)
        };

        foreach ((int age, AttributeCategory category, float expected) in cases)
        {
            Assert.Equal(expected, GrowthEngine.AgeCurveMultiplier(age, category));
        }
    }

    [Fact]
    public void AgeCurveMultiplier_ThrowsForAnUndefinedCategory()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GrowthEngine.AgeCurveMultiplier(20, (AttributeCategory)99));
    }

    [Fact]
    public void SoftCeilingFactor_ClampsToThePlanBand()
    {
        Assert.Equal(0f, GrowthEngine.SoftCeilingFactor(-12f));
        Assert.Equal(0f, GrowthEngine.SoftCeilingFactor(0f));
        AssertClose(0.08f, GrowthEngine.SoftCeilingFactor(0.5f), 1e-6f, "tiny gap clamps to the floor");
        AssertClose(0.08f, GrowthEngine.SoftCeilingFactor(1.44f), 1e-6f, "gap at the floor boundary");
        AssertClose(0.5f, GrowthEngine.SoftCeilingFactor(9f), 1e-6f, "half the reference gap");
        AssertClose(1.0f, GrowthEngine.SoftCeilingFactor(18f), 1e-6f, "reference gap opens the factor");
        AssertClose(1.0f, GrowthEngine.SoftCeilingFactor(60f), 1e-6f, "large gaps stay capped at 1");
    }

    [Fact]
    public void WeeklyDeclinePerWeek_MatchesTheCategoryOnsetsAndCaps()
    {
        (int Age, AttributeId Attribute, float Expected)[] cases =
        {
            // Innate onset 28; +0.010 a year, capped at 0.08.
            (27, AttributeId.TopSpeed, 0.000f),
            (28, AttributeId.TopSpeed, 0.020f),
            (29, AttributeId.TopSpeed, 0.030f),
            (34, AttributeId.TopSpeed, 0.080f),
            (40, AttributeId.TopSpeed, 0.080f),

            // Acceleration's per-attribute DeclineOnsetAge is 27, but the documented category onset (28) wins.
            (27, AttributeId.Acceleration, 0.000f),
            (28, AttributeId.Acceleration, 0.020f),

            // Body-trainable onset 30; +0.008 a year, capped at 0.06.
            (29, AttributeId.Strength, 0.000f),
            (30, AttributeId.Strength, 0.015f),
            (31, AttributeId.Strength, 0.023f),
            (35, AttributeId.Strength, 0.055f),
            (36, AttributeId.Strength, 0.060f),
            (42, AttributeId.Strength, 0.060f),

            // Agility's per-attribute DeclineOnsetAge is 29, but the category onset (30) wins.
            (29, AttributeId.Agility, 0.000f),

            // Skill-and-mind onset 34; +0.005 a year, capped at 0.04.
            (33, AttributeId.Passing, 0.000f),
            (34, AttributeId.Passing, 0.010f),
            (35, AttributeId.Passing, 0.015f),
            (40, AttributeId.Passing, 0.040f),
            (45, AttributeId.Passing, 0.040f)
        };

        foreach ((int age, AttributeId attribute, float expected) in cases)
        {
            AssertClose(
                expected,
                GrowthEngine.WeeklyDeclinePerWeek(age, attribute),
                1e-4f,
                $"{attribute} decline at age {age}");
        }

        // Never negative, for every attribute and every supported age.
        for (int age = 14; age <= 45; age++)
        {
            foreach (AttributeId id in AttributeMetadata.All)
            {
                Assert.True(
                    GrowthEngine.WeeklyDeclinePerWeek(age, id) >= 0f,
                    $"{id} decline at age {age} must never be negative");
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(
            () => GrowthEngine.WeeklyDeclinePerWeek(30, (AttributeId)AttributeCount));
    }

    // =============================================================================================
    // 3. Gate: category dynamics over four seasons.
    // =============================================================================================

    [Fact]
    public void CategoryDynamics_OverFourSeasons_SkillMovesFarMoreThanInnateAndBodyTrailsSkill()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            31UL, 16, BackstoryKind.RegionalAcademy);

        // Give the skill-and-mind category a deliberately large runway: the gate is about category response,
        // not about how one seed happened to roll its ceilings.
        ForceSkillCategory(player, ceiling: 80f, current: 40f);

        float innateStart = player.Attributes.MeanCurrent(AttributeCategory.Innate);
        float bodyStart = player.Attributes.MeanCurrent(AttributeCategory.BodyTrainable);
        float skillStart = player.Attributes.MeanCurrent(AttributeCategory.SkillAndMind);

        // Four seasons of a 16-year-old's development: full matches at an average 7.0 rating.
        AdvanceWeeks(player, TrainingFocus.Balanced, minutes: 60, rating: 7.0f, seed: 77UL, weeks: CareerWeeks);

        float innateGain = player.Attributes.MeanCurrent(AttributeCategory.Innate) - innateStart;
        float bodyGain = player.Attributes.MeanCurrent(AttributeCategory.BodyTrainable) - bodyStart;
        float skillGain = player.Attributes.MeanCurrent(AttributeCategory.SkillAndMind) - skillStart;

        Assert.True(
            skillGain >= 12f,
            $"skill-and-mind mean gain was {skillGain:0.0}; four seasons of training should move it substantially");
        Assert.True(
            MathF.Abs(innateGain) <= 2f,
            $"innate mean moved {innateGain:0.00}; training must barely touch the innate category");
        Assert.True(
            bodyGain > 0f,
            $"body-trainable should still develop, but its mean moved {bodyGain:0.00}");
        Assert.True(
            skillGain > bodyGain,
            $"skill gain {skillGain:0.0} should clearly exceed body gain {bodyGain:0.0}");
    }

    // =============================================================================================
    // 4. Age decline and physical ceiling aging.
    // =============================================================================================

    [Fact]
    public void InnateDecline_PastTwentyEight_LowersCurrentsAndCeilings()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            41UL, 30, BackstoryKind.LocalClubYouth);

        float innateMeanStart = player.Attributes.MeanCurrent(AttributeCategory.Innate);
        float[] innateCeilingsStart = CeilingsOf(player, AttributeCategory.Innate);

        // A full year past the innate decline onset: 52 weeks of aging, no match load needed.
        AdvanceWeeks(player, TrainingFocus.Balanced, minutes: 0, rating: 0f, seed: 88UL, weeks: 52);

        float innateMeanEnd = player.Attributes.MeanCurrent(AttributeCategory.Innate);
        Assert.True(
            innateMeanEnd < innateMeanStart - 1f,
            $"innate mean only fell {innateMeanStart - innateMeanEnd:0.00} in a year past 28");

        IReadOnlyList<AttributeId> innateIds = AttributeMetadata.OfCategory(AttributeCategory.Innate);
        float ceilingDrop = 0f;
        for (int i = 0; i < innateIds.Count; i++)
        {
            float end = player.Attributes.Ceiling(innateIds[i]);
            Assert.True(
                end <= innateCeilingsStart[i],
                $"{innateIds[i]} ceiling rose from {innateCeilingsStart[i]} to {end} while aging");
            ceilingDrop += innateCeilingsStart[i] - end;
        }

        Assert.True(ceilingDrop > 0.2f, $"physical ceiling aging pressure only removed {ceilingDrop:0.00}");
    }

    // =============================================================================================
    // 5. Body-trainable trade-offs.
    // =============================================================================================

    [Fact]
    public void StrengthFocus_AtStrength73_DragsAgility()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            51UL, 22, BackstoryKind.LocalClubYouth);

        // Pin both attributes at their ceilings so this week's only possible change is the trade-off.
        ForceAttribute(player, AttributeId.Strength, ceiling: 73f, current: 73f);
        ForceAttribute(player, AttributeId.Agility, ceiling: 60f, current: 60f);

        GrowthEngine.AdvanceWeek(
            player, TrainingEnvironment.ForTier(1), TrainingFocus.StrengthAndPower, 0, 0f, GrowthRng(5UL));

        AssertClose(
            60f - 0.04f,
            player.Attributes.Current(AttributeId.Agility),
            1e-5f,
            "over-bulk strength focus should drag agility by 0.04");
    }

    [Fact]
    public void StrengthFocus_AtStrength79_TaxesTopSpeedCeilingHeadroomNotCurrent()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            52UL, 22, BackstoryKind.LocalClubYouth);

        // Innate gain is already zero at 22, so TopSpeed's current is untouched by training; keep Strength at
        // its ceiling so it does not grow into the over-bulk band this week.
        ForceAttribute(player, AttributeId.Strength, ceiling: 79f, current: 79f);

        float ceilingBefore = player.Attributes.Ceiling(AttributeId.TopSpeed);
        float currentBefore = player.Attributes.Current(AttributeId.TopSpeed);

        GrowthEngine.AdvanceWeek(
            player, TrainingEnvironment.ForTier(1), TrainingFocus.StrengthAndPower, 0, 0f, GrowthRng(6UL));

        AssertClose(
            ceilingBefore - 0.015f,
            player.Attributes.Ceiling(AttributeId.TopSpeed),
            1e-6f,
            "over-bulk top-speed ceiling headroom tax");
        Assert.Equal(
            Bits(currentBefore), Bits(player.Attributes.Current(AttributeId.TopSpeed)));
    }

    [Fact]
    public void AgilityFocus_AtStrength71_DragsStrength()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            53UL, 22, BackstoryKind.LocalClubYouth);

        // Pin Strength at its ceiling so training cannot lift it out of the drag band during the week.
        ForceAttribute(player, AttributeId.Strength, ceiling: 71f, current: 71f);
        ForceAttribute(player, AttributeId.Agility, ceiling: 60f, current: 60f);

        GrowthEngine.AdvanceWeek(
            player, TrainingEnvironment.ForTier(1), TrainingFocus.AgilityAndQuickness, 0, 0f, GrowthRng(7UL));

        AssertClose(
            71f - 0.03f,
            player.Attributes.Current(AttributeId.Strength),
            1e-5f,
            "agility focus should drag strength by 0.03");
    }

    // =============================================================================================
    // 6. Match minutes.
    // =============================================================================================

    [Fact]
    public void MatchMinutes_AccelerateSkillGrowth_AndKeepSharpnessHigher()
    {
        const ulong playerSeed = 61UL;
        WorldPlayer idle = PlayerGenerator.GenerateAuthentic(
            playerSeed, 18, BackstoryKind.RegionalAcademy);
        WorldPlayer starter = PlayerGenerator.GenerateAuthentic(
            playerSeed, 18, BackstoryKind.RegionalAcademy);

        foreach (WorldPlayer player in new[] { idle, starter })
        {
            ForceSkillCategory(player, ceiling: 75f, current: 30f);
            player.Condition01 = 0.8f;
            player.MatchSharpness01 = 0.6f;
        }

        float idleStart = idle.Attributes.MeanCurrent(AttributeCategory.SkillAndMind);
        float starterStart = starter.Attributes.MeanCurrent(AttributeCategory.SkillAndMind);
        AssertClose(idleStart, starterStart, 1e-6f, "the two scenarios must start identical");

        AdvanceWeeks(idle, TrainingFocus.Balanced, minutes: 0, rating: 0f, seed: 62UL, weeks: 8);
        AdvanceWeeks(starter, TrainingFocus.Balanced, minutes: 90, rating: 7.0f, seed: 62UL, weeks: 8);

        float idleGain = idle.Attributes.MeanCurrent(AttributeCategory.SkillAndMind) - idleStart;
        float starterGain = starter.Attributes.MeanCurrent(AttributeCategory.SkillAndMind) - starterStart;

        Assert.True(
            starterGain > idleGain + 0.3f,
            $"90 minutes of football must out-grow a week of no minutes: {starterGain:0.00} vs {idleGain:0.00}");
        Assert.True(
            starter.MatchSharpness01 > idle.MatchSharpness01 + 0.3f,
            $"minutes must build sharpness: {starter.MatchSharpness01:0.00} vs {idle.MatchSharpness01:0.00}");
    }

    [Fact]
    public void ConditionAndSharpness_TrackMinutesAndRecoveryFocus()
    {
        WorldPlayer idle = FreshPlayer(71UL, 24);
        idle.Condition01 = 0.8f;
        idle.MatchSharpness01 = 0.6f;
        GrowthWeekResult idleResult = GrowthEngine.AdvanceWeek(
            idle, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 0, 0f, GrowthRng(72UL));

        AssertClose(-0.06f, idleResult.SharpnessDelta, 1e-5f, "no minutes rusts sharpness");
        AssertClose(0.54f, idle.MatchSharpness01, 1e-5f, "sharpness after a 0-minute week");
        AssertClose(0.18f, idleResult.ConditionDelta, 1e-5f, "no load restores condition");
        AssertClose(0.98f, idle.Condition01, 1e-5f, "condition after a 0-minute week");

        WorldPlayer full = FreshPlayer(71UL, 24);
        full.Condition01 = 0.8f;
        full.MatchSharpness01 = 0.6f;
        GrowthWeekResult fullResult = GrowthEngine.AdvanceWeek(
            full, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 90, 7.0f, GrowthRng(72UL));

        AssertClose(0.10f, fullResult.SharpnessDelta, 1e-5f, "a full match builds sharpness");
        AssertClose(0.70f, full.MatchSharpness01, 1e-5f, "sharpness after a 90-minute week");
        AssertClose(-0.12f, fullResult.ConditionDelta, 1e-5f, "a full match spends condition");
        AssertClose(0.68f, full.Condition01, 1e-5f, "condition after a 90-minute week");

        Assert.True(full.MatchSharpness01 > idle.MatchSharpness01, "playing must leave the player sharper");
        Assert.True(full.Condition01 < idle.Condition01, "playing must leave the player more tired");

        WorldPlayer recovery = FreshPlayer(71UL, 24);
        recovery.Condition01 = 0.8f;
        recovery.MatchSharpness01 = 0.6f;
        GrowthWeekResult recoveryResult = GrowthEngine.AdvanceWeek(
            recovery, TrainingEnvironment.ForTier(1), TrainingFocus.RecoveryAndLight, 90, 7.0f, GrowthRng(72UL));

        AssertClose(
            -0.02f, recoveryResult.ConditionDelta, 1e-5f, "recovery work cuts the load penalty by 0.10");
        AssertClose(0.78f, recovery.Condition01, 1e-5f, "condition after recovery work");
        Assert.True(
            recoveryResult.ConditionDelta > fullResult.ConditionDelta,
            "RecoveryAndLight must recover more condition than Balanced");
        Assert.True(
            recovery.MatchSharpness01 > full.MatchSharpness01,
            "recovery work adds a little sharpness on top of match minutes");
    }

    [Fact]
    public void InjuredPlayers_TrainAtRehabRate_AndRecoverInsteadOfTakingLoad()
    {
        const ulong playerSeed = 151UL;
        WorldPlayer healthy = PlayerGenerator.GenerateAuthentic(
            playerSeed, 19, BackstoryKind.RegionalAcademy);
        WorldPlayer injured = PlayerGenerator.GenerateAuthentic(
            playerSeed, 19, BackstoryKind.RegionalAcademy);

        foreach (WorldPlayer player in new[] { healthy, injured })
        {
            ForceSkillCategory(player, ceiling: 75f, current: 30f);
            player.Condition01 = 0.7f;
            player.MatchSharpness01 = 0.6f;
        }

        injured.Injury.Begin(InjuryKind.HamstringTear, InjurySeverity.Moderate, 4);

        float healthyStart = healthy.Attributes.MeanCurrent(AttributeCategory.SkillAndMind);
        float injuredStart = injured.Attributes.MeanCurrent(AttributeCategory.SkillAndMind);

        // Neither plays: an injured player cannot take the pitch. Same RNG seed so the only difference is rehab.
        GrowthWeekResult healthyResult = GrowthEngine.AdvanceWeek(
            healthy, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 0, 0f, GrowthRng(152UL));
        GrowthWeekResult injuredResult = GrowthEngine.AdvanceWeek(
            injured, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 0, 0f, GrowthRng(152UL));

        float healthyGain = healthy.Attributes.MeanCurrent(AttributeCategory.SkillAndMind) - healthyStart;
        float injuredGain = injured.Attributes.MeanCurrent(AttributeCategory.SkillAndMind) - injuredStart;

        Assert.True(injuredGain > 0f, "an injured player still trains at the rehab rate");
        AssertClose(
            healthyGain * 0.25f, injuredGain, 1e-6f, "rehab is a quarter of the training stimulus");
        AssertClose(
            0.10f, injuredResult.ConditionDelta, 1e-5f, "rehab recovery replaces the match load term");
        Assert.True(
            injuredResult.ConditionDelta < healthyResult.ConditionDelta,
            "a fit player at rest recovers more condition than an injured one");
    }

    // =============================================================================================
    // 7. Gate: soft skill-ceiling nudges.
    // =============================================================================================

    [Fact]
    public void SoftCeilingNudges_SustainedHighRatings_RaiseSkillCeilingsWithinTheCareerCap()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            81UL, 22, BackstoryKind.EliteAcademy);

        // Eight recent 90-minute 8.0 displays: form >= 7.6 and sustained minutes both satisfied.
        for (int week = 1; week <= 8; week++)
        {
            player.Form.Record(new MatchAppearance(1, week, 90, 8.0f));
        }

        // One skill attribute sits inside the four-point window, so it is eligible for a nudge.
        ForceAttribute(player, AttributeId.Passing, ceiling: 70f, current: 68f);

        // Pre-load almost all of the lifetime +8 allowance: the engine may hand out at most 0.5 more.
        player.RecordCeilingNudge(AttributeId.Passing, 7.5f);
        float passingCeilingStart = player.Attributes.Ceiling(AttributeId.Passing);

        float[] innateCeilingsStart = CeilingsOf(player, AttributeCategory.Innate);
        float[] bodyCeilingsStart = CeilingsOf(player, AttributeCategory.BodyTrainable);

        WorldRng rng = GrowthRng(82UL);
        int totalNudges = 0;
        for (int week = 0; week < 30; week++)
        {
            GrowthWeekResult result = GrowthEngine.AdvanceWeek(
                player, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 90, 8.0f, rng);

            Assert.InRange(result.CeilingNudgesApplied, 0, 2);
            totalNudges += result.CeilingNudgesApplied;

            // Physical ceilings are never nudged upward by form: only skill-and-mind ceilings move here.
            AssertCeilingsUnchanged(player, AttributeCategory.Innate, innateCeilingsStart);
            AssertCeilingsUnchanged(player, AttributeCategory.BodyTrainable, bodyCeilingsStart);
        }

        float passingNudgeApplied = player.CeilingNudgeApplied(AttributeId.Passing);
        Assert.True(totalNudges >= 1, "sustained 8.0 ratings should grant at least one ceiling nudge in 30 weeks");
        Assert.True(
            passingNudgeApplied > 7.5f,
            $"the eligible skill attribute should have received a nudge, ledger is {passingNudgeApplied}");
        Assert.True(
            passingNudgeApplied <= WorldPlayer.MaxCareerCeilingNudge + 1e-4f,
            $"the lifetime cap of {WorldPlayer.MaxCareerCeilingNudge} was exceeded: {passingNudgeApplied}");

        AssertClose(
            passingCeilingStart + (passingNudgeApplied - 7.5f),
            player.Attributes.Ceiling(AttributeId.Passing),
            1e-3f,
            "the ceiling rise must match the recorded nudge");

        // No physical attribute may ever record a skill nudge.
        foreach (AttributeId id in AttributeMetadata.All)
        {
            AttributeCategory category = AttributeMetadata.Get(id).Category;
            if (category == AttributeCategory.SkillAndMind) continue;
            Assert.Equal(0f, player.CeilingNudgeApplied(id));
        }

        Assert.True(player.Attributes.IsValid(), "the player must stay valid after a month of nudges");
    }

    // =============================================================================================
    // 8. Permanent injury damage.
    // =============================================================================================

    [Fact]
    public void PermanentInjuryDamage_KneeLigament_ShavesPhysicalCeilingsOnce()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            91UL, 24, BackstoryKind.LocalClubYouth);
        player.Injury.Begin(InjuryKind.KneeLigament, InjurySeverity.Severe, 12);

        float[] before = Ceilings(player);
        GrowthEngine.ApplyPermanentInjuryDamage(player, InjuryKind.KneeLigament, GrowthRng(92UL));
        float[] after = Ceilings(player);

        AssertInShaveRange(before, after, AttributeId.TopSpeed, 3.0f, 6.0f);
        AssertInShaveRange(before, after, AttributeId.Acceleration, 2.5f, 5.0f);
        AssertInShaveRange(before, after, AttributeId.Agility, 2.0f, 4.0f);

        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (id is AttributeId.TopSpeed or AttributeId.Acceleration or AttributeId.Agility) continue;
            Assert.Equal(Bits(before[(int)id]), Bits(after[(int)id]));
        }

        Assert.Equal(1, player.Injury.PermanentDamageApplied);
        Assert.True(player.Attributes.IsValid());

        // A second call for the same injury must be a complete no-op.
        float[] afterFirst = Ceilings(player);
        GrowthEngine.ApplyPermanentInjuryDamage(player, InjuryKind.KneeLigament, GrowthRng(93UL));
        float[] afterSecond = Ceilings(player);
        for (int i = 0; i < AttributeCount; i++)
        {
            Assert.Equal(Bits(afterFirst[i]), Bits(afterSecond[i]));
        }

        Assert.Equal(1, player.Injury.PermanentDamageApplied);
    }

    [Fact]
    public void PermanentInjuryDamage_EachKind_ShavesOnlyItsOwnCeilingsWithinRanges()
    {
        for (int row = 0; row < PermanentDamageTable.Length; row++)
        {
            (InjuryKind kind, (AttributeId Id, float Min, float Max)[] shaves) = PermanentDamageTable[row];
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                200UL + (ulong)row, 25, BackstoryKind.RegionalAcademy);
            if (kind != InjuryKind.None)
            {
                player.Injury.Begin(kind, InjurySeverity.Severe, 10);
            }

            float[] before = Ceilings(player);
            GrowthEngine.ApplyPermanentInjuryDamage(player, kind, GrowthRng(300UL + (ulong)row));
            float[] after = Ceilings(player);

            bool hasDamage = shaves.Length > 0;
            foreach ((AttributeId id, float min, float max) in shaves)
            {
                AssertInShaveRange(before, after, id, min, max);
            }

            // Every attribute not named in this injury's row is untouched, bit for bit.
            foreach (AttributeId id in AttributeMetadata.All)
            {
                if (ContainsShave(shaves, id)) continue;
                Assert.Equal(Bits(before[(int)id]), Bits(after[(int)id]));
            }

            Assert.Equal(hasDamage ? 1 : 0, player.Injury.PermanentDamageApplied);
            Assert.True(player.Attributes.IsValid());
        }
    }

    // =============================================================================================
    // 9. Result bookkeeping and trend history.
    // =============================================================================================

    [Fact]
    public void GrowthWeekResult_ReportsTrainingGainDeclineAndWeekChanges()
    {
        // An aged player pinned at every ceiling: no training gain, only decline and aging pressure.
        WorldPlayer veteran = PlayerGenerator.GenerateAuthentic(
            141UL, 34, BackstoryKind.LocalClubYouth);
        FillCurrentsToCeilings(veteran);
        veteran.Condition01 = 0.5f;
        veteran.MatchSharpness01 = 0.6f;

        GrowthWeekResult veteranResult = GrowthEngine.AdvanceWeek(
            veteran, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 0, 0f, GrowthRng(142UL));

        AssertClose(0f, veteranResult.TrainingGain, 1e-6f, "a capped player cannot gain");
        Assert.Equal(0, veteranResult.AttributesImproved);
        Assert.Equal(0, veteranResult.CeilingNudgesApplied);
        AssertClose(0.18f, veteranResult.ConditionDelta, 1e-5f, "condition bookkeeping");
        AssertClose(-0.06f, veteranResult.SharpnessDelta, 1e-5f, "sharpness bookkeeping");

        // Age 34 decline: 3 innate * 0.080 + 4 body * 0.047 + 11 skill * 0.010 = 0.538 points.
        AssertClose(0.538f, veteranResult.DeclineApplied, 1e-3f, "decline bookkeeping");

        // A young player with a real gap: training gain is counted and the improved count is the net measure.
        WorldPlayer prospect = PlayerGenerator.GenerateAuthentic(
            143UL, 18, BackstoryKind.EliteAcademy);
        float[] before = prospect.Attributes.CurrentValues();

        GrowthWeekResult prospectResult = GrowthEngine.AdvanceWeek(
            prospect, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 60, 7.0f, GrowthRng(144UL));

        float positiveDeltaSum = 0f;
        int improvedManually = 0;
        for (int i = 0; i < AttributeCount; i++)
        {
            float delta = prospect.Attributes.Current((AttributeId)i) - before[i];
            if (delta > 0f)
            {
                improvedManually++;
                positiveDeltaSum += delta;
            }
        }

        Assert.Equal(improvedManually, prospectResult.AttributesImproved);
        Assert.True(prospectResult.TrainingGain > 0f, "a young player with a gap must show training gain");
        AssertClose(positiveDeltaSum, prospectResult.TrainingGain, 1e-4f, "training gain bookkeeping");
    }

    [Fact]
    public void Trends_RecordOneSnapshotPerWeek_AndDeltasMatchTheSnapshots()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            101UL, 20, BackstoryKind.RegionalAcademy);
        WorldRng rng = GrowthRng(102UL);

        for (int week = 1; week <= 3; week++)
        {
            GrowthEngine.AdvanceWeek(
                player, TrainingEnvironment.ForTier(1), TrainingFocus.Balanced, 45, 6.5f, rng);
            Assert.Equal(week, player.Trends.Count);
        }

        IReadOnlyList<float[]> snapshots = player.Trends.Snapshots;
        float[] current = player.Attributes.CurrentValues();
        int last = snapshots.Count - 1;

        foreach (AttributeId id in AttributeMetadata.All)
        {
            int index = (int)id;
            Assert.Equal(Bits(current[index]), Bits(snapshots[last][index]));
            AssertClose(
                snapshots[last][index] - snapshots[last - 1][index],
                player.Trends.Delta(id, 1),
                1e-6f,
                $"{id} one-week trend delta");
            AssertClose(
                snapshots[last][index] - snapshots[0][index],
                player.Trends.Delta(id, 2),
                1e-6f,
                $"{id} two-week trend delta");
            AssertClose(
                snapshots[last][index] - snapshots[0][index],
                player.Trends.Delta(id, player.Trends.Count + 5),
                1e-6f,
                $"{id} trend delta when the window is shorter than requested");
        }
    }

    // =============================================================================================
    // 10. Determinism and numerical safety.
    // =============================================================================================

    [Fact]
    public void Determinism_SameSeedAndCallSequence_ProduceBitIdenticalPlayers()
    {
        const ulong playerSeed = 111UL;
        WorldPlayer first = PlayerGenerator.GenerateAuthentic(
            playerSeed, 19, BackstoryKind.EliteAcademy);
        WorldPlayer second = PlayerGenerator.GenerateAuthentic(
            playerSeed, 19, BackstoryKind.EliteAcademy);

        foreach (WorldPlayer player in new[] { first, second })
        {
            for (int week = 1; week <= 8; week++)
            {
                player.Form.Record(new MatchAppearance(1, week, 90, 8.0f));
            }
        }

        WorldRng firstRng = GrowthRng(112UL);
        WorldRng secondRng = GrowthRng(112UL);
        TrainingFocus[] focuses =
        {
            TrainingFocus.Balanced,
            TrainingFocus.StrengthAndPower,
            TrainingFocus.PassingAndVision,
            TrainingFocus.RecoveryAndLight
        };

        for (int week = 0; week < 20; week++)
        {
            bool rested = week % 3 == 0;
            int minutes = rested ? 0 : 60;
            float rating = rested ? 0f : 7.8f;
            TrainingFocus focus = focuses[week % focuses.Length];

            GrowthWeekResult firstResult = GrowthEngine.AdvanceWeek(
                first, TrainingEnvironment.ForTier(1), focus, minutes, rating, firstRng);
            GrowthWeekResult secondResult = GrowthEngine.AdvanceWeek(
                second, TrainingEnvironment.ForTier(1), focus, minutes, rating, secondRng);

            Assert.Equal(firstResult, secondResult);
        }

        AssertAttributeStateBitsEqual(first, second);
        Assert.Equal(Bits(first.Condition01), Bits(second.Condition01));
        Assert.Equal(Bits(first.MatchSharpness01), Bits(second.MatchSharpness01));
        Assert.Equal(Bits(first.Form01), Bits(second.Form01));
        Assert.Equal(first.Trends.Count, second.Trends.Count);
    }

    [Fact]
    public void AdvanceWeek_FourSeasons_NeverProducesNaNOrAnInvalidAttributeSet()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            121UL, 17, BackstoryKind.StreetFootball);
        WorldRng rng = GrowthRng(122UL);
        TrainingEnvironment environment = TrainingEnvironment.ForTier(2);
        TrainingFocus[] focusCycle =
        {
            TrainingFocus.Balanced,
            TrainingFocus.StrengthAndPower,
            TrainingFocus.EnduranceAndEngine,
            TrainingFocus.AgilityAndQuickness,
            TrainingFocus.RecoveryAndLight,
            TrainingFocus.PassingAndVision,
            TrainingFocus.DefendingAndPositioning,
            TrainingFocus.FinishingAndShooting
        };

        for (int week = 0; week < CareerWeeks; week++)
        {
            int minutes = week % 4 == 0 ? 0 : 90;
            float rating = minutes == 0 ? 0f : 6.5f + week % 5 * 0.5f;

            GrowthWeekResult result = GrowthEngine.AdvanceWeek(
                player, environment, focusCycle[week % focusCycle.Length], minutes, rating, rng);

            AssertFinite(result.TrainingGain, $"{week}: TrainingGain");
            AssertFinite(result.DeclineApplied, $"{week}: DeclineApplied");
            AssertFinite(result.ConditionDelta, $"{week}: ConditionDelta");
            AssertFinite(result.SharpnessDelta, $"{week}: SharpnessDelta");
            Assert.True(player.Attributes.IsValid(), $"week {week}: attribute set is invalid");

            foreach (AttributeId id in AttributeMetadata.All)
            {
                AssertFinite(player.Attributes.Current(id), $"week {week}: {id} current");
                AssertFinite(player.Attributes.Ceiling(id), $"week {week}: {id} ceiling");
                AssertFinite(player.Attributes.Plasticity(id), $"week {week}: {id} plasticity");
            }
        }

        Assert.InRange(player.Condition01, 0f, 1f);
        Assert.InRange(player.MatchSharpness01, 0f, 1f);
        Assert.InRange(player.Form01, 0f, 1f);

        // The trend history is a bounded rolling window: one snapshot a week, at most WindowWeeks + 1 kept.
        Assert.Equal(PlayerTrendHistory.WindowWeeks + 1, player.Trends.Count);
    }

    // =============================================================================================
    // 11. Validation.
    // =============================================================================================

    [Fact]
    public void AdvanceWeek_RejectsInvalidInputs()
    {
        TrainingEnvironment valid = TrainingEnvironment.ForTier(1);

        Assert.Throws<ArgumentNullException>(
            () => GrowthEngine.AdvanceWeek(null!, valid, TrainingFocus.Balanced, 0, 0f, GrowthRng(1UL)));
        Assert.Throws<ArgumentNullException>(
            () => GrowthEngine.AdvanceWeek(FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, 0, 0f, null!));

        // Tier outside 1..6.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), new TrainingEnvironment(0, 1.0f, 5), TrainingFocus.Balanced, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), new TrainingEnvironment(7, 1.0f, 5), TrainingFocus.Balanced, 0, 0f));

        // Coaching quality outside the 0.70..1.30 club band (the tier presets are exempt by identity).
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), new TrainingEnvironment(2, 0.69f, 5), TrainingFocus.Balanced, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), new TrainingEnvironment(2, 1.31f, 5), TrainingFocus.Balanced, 0, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), new TrainingEnvironment(2, float.NaN, 5), TrainingFocus.Balanced, 0, 0f));

        // Minutes outside 0..180.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, -1, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, 181, 0f));

        // Rating outside 0..10.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, 0, -0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, 0, 10.1f));

        // A player with minutes needs a real 4.0..10.0 match rating.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, TrainingFocus.Balanced, 30, 3.0f));

        // Undefined focus preset.
        Assert.Throws<ArgumentOutOfRangeException>(() => AdvanceWith(
            FreshPlayer(131UL, 20), valid, (TrainingFocus)99, 0, 0f));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    private static WorldPlayer FreshPlayer(ulong seed, int age) =>
        PlayerGenerator.GenerateAuthentic(seed, age, BackstoryKind.RegionalAcademy);

    private static WorldRng GrowthRng(ulong seed) => WorldRng.For(seed, WorldRandomStream.Growth);

    private static GrowthWeekResult AdvanceWith(
        WorldPlayer player,
        TrainingEnvironment environment,
        TrainingFocus focus,
        int minutes,
        float rating) =>
        GrowthEngine.AdvanceWeek(player, environment, focus, minutes, rating, GrowthRng(999UL));

    private static void AdvanceWeeks(
        WorldPlayer player,
        TrainingFocus focus,
        int minutes,
        float rating,
        ulong seed,
        int weeks,
        TrainingEnvironment? environment = null)
    {
        TrainingEnvironment env = environment ?? TrainingEnvironment.ForTier(1);
        WorldRng rng = GrowthRng(seed);
        for (int week = 0; week < weeks; week++)
        {
            GrowthEngine.AdvanceWeek(player, env, focus, minutes, rating, rng);
        }
    }

    /// <summary>Pins an attribute's ceiling and current value, used to isolate one single effect per week.</summary>
    private static void ForceAttribute(WorldPlayer player, AttributeId id, float ceiling, float current)
    {
        player.Attributes.SetCeiling(id, ceiling);
        player.Attributes.SetCurrent(id, current);
    }

    /// <summary>Gives every skill-and-mind attribute the same ceiling/current, i.e. the same development runway.</summary>
    private static void ForceSkillCategory(WorldPlayer player, float ceiling, float current)
    {
        foreach (AttributeId id in AttributeMetadata.OfCategory(AttributeCategory.SkillAndMind))
        {
            ForceAttribute(player, id, ceiling, current);
        }
    }

    private static void FillCurrentsToCeilings(WorldPlayer player)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCurrent(id, player.Attributes.Ceiling(id));
        }
    }

    private static float[] Ceilings(WorldPlayer player)
    {
        var result = new float[AttributeCount];
        foreach (AttributeId id in AttributeMetadata.All)
        {
            result[(int)id] = player.Attributes.Ceiling(id);
        }

        return result;
    }

    private static float[] CeilingsOf(WorldPlayer player, AttributeCategory category)
    {
        IReadOnlyList<AttributeId> ids = AttributeMetadata.OfCategory(category);
        var result = new float[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            result[i] = player.Attributes.Ceiling(ids[i]);
        }

        return result;
    }

    private static void AssertCeilingsUnchanged(
        WorldPlayer player, AttributeCategory category, float[] expected)
    {
        IReadOnlyList<AttributeId> ids = AttributeMetadata.OfCategory(category);
        for (int i = 0; i < ids.Count; i++)
        {
            Assert.Equal(Bits(expected[i]), Bits(player.Attributes.Ceiling(ids[i])));
        }
    }

    private static void AssertInShaveRange(
        float[] before, float[] after, AttributeId id, float minimum, float maximum)
    {
        float shaved = before[(int)id] - after[(int)id];
        Assert.InRange(shaved, minimum - 1e-3f, maximum + 1e-3f);
    }

    private static bool ContainsShave(
        (AttributeId Id, float Min, float Max)[] shaves, AttributeId id)
    {
        foreach ((AttributeId candidate, _, _) in shaves)
        {
            if (candidate == id) return true;
        }

        return false;
    }

    private static void AssertAttributeStateBitsEqual(WorldPlayer expected, WorldPlayer actual)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            Assert.Equal(
                Bits(expected.Attributes.Current(id)), Bits(actual.Attributes.Current(id)));
            Assert.Equal(
                Bits(expected.Attributes.Ceiling(id)), Bits(actual.Attributes.Ceiling(id)));
            Assert.Equal(
                Bits(expected.Attributes.Plasticity(id)), Bits(actual.Attributes.Plasticity(id)));
        }
    }

    private static void AssertFinite(float value, string because) =>
        Assert.True(float.IsFinite(value), $"{because} must be finite, was {value}");

    private static void AssertClose(
        float expected, float actual, float tolerance, string because)
    {
        Assert.True(
            MathF.Abs(expected - actual) <= tolerance,
            $"{because}: expected {expected:0.######}, got {actual:0.######}");
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
}
