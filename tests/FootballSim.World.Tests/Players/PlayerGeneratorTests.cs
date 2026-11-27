using System;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Players;

/// <summary>
/// W1 player-generation contract: deterministic two-mode creation, the hard "backstory never moves a
/// ceiling" invariant, the age runway, goalkeeper handling, the population pyramid and tiered NPC squads.
/// Everything here is a pure function of a seed, so every assertion is reproducible from the test name.
/// </summary>
public sealed class PlayerGeneratorTests
{
    private const int AttributeCount = 18;

    // =============================================================================================
    // Determinism and identity independence.
    // =============================================================================================

    [Fact]
    public void SameArguments_ProduceBitIdenticalPlayers()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            BackstoryKind backstory = (BackstoryKind)(seed % 5);
            int age = 15 + (int)(seed % 24);
            bool keeper = seed % 2 == 0;

            WorldPlayer first = PlayerGenerator.GenerateAuthentic(seed, age, backstory, keeper);
            WorldPlayer second = PlayerGenerator.GenerateAuthentic(seed, age, backstory, keeper);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.FirstName, second.FirstName);
            Assert.Equal(first.LastName, second.LastName);
            AssertSameGeneratedValues(first, second);
        }
    }

    [Fact]
    public void IdentityArguments_DoNotChangeAnyGeneratedValue()
    {
        WorldPlayer anonymous = PlayerGenerator.GenerateAuthentic(7UL, 17, BackstoryKind.RegionalAcademy);
        WorldPlayer named = PlayerGenerator.GenerateAuthentic(
            7UL, 17, BackstoryKind.RegionalAcademy, false, new PlayerId(42), "Ada", "Valdis");

        AssertSameGeneratedValues(anonymous, named);
        Assert.Equal(new PlayerId(42), named.Id);
        Assert.Equal("Ada", named.FirstName);
        Assert.Equal("Valdis", named.LastName);
        Assert.Equal("Ada Valdis", named.DisplayName);

        WorldPlayer stillAnonymous = PlayerGenerator.GenerateAuthentic(
            7UL, 17, BackstoryKind.RegionalAcademy, false, PlayerId.None, "", "");
        AssertSameGeneratedValues(anonymous, stillAnonymous);
        Assert.Equal(string.Empty, stillAnonymous.DisplayName);

        WorldPlayer custom = PlayerGenerator.GenerateCustom(
            11UL, 18, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth);
        WorldPlayer customNamed = PlayerGenerator.GenerateCustom(
            11UL, 18, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth, false, new PlayerId(3), "Bo", "");
        AssertSameGeneratedValues(custom, customNamed);

        WorldPlayer npc = PlayerGenerator.GenerateNpcForTier(5UL, 3, 21, TacticalRole.Winger);
        WorldPlayer npcNamed = PlayerGenerator.GenerateNpcForTier(
            5UL, 3, 21, TacticalRole.Winger, new PlayerId(9), "Nils", "Sorren");
        AssertSameGeneratedValues(npc, npcNamed);
        Assert.Equal(new PlayerId(9), npcNamed.Id);
    }

    // =============================================================================================
    // Gate: ceilings are rolled before the backstory and therefore never depend on it.
    // =============================================================================================

    [Fact]
    public void Ceilings_AreIndependentOfBackstory_WhileRealizationAndVisibilityDiffer()
    {
        const int SeedCount = 25;
        int currentDifferenceSeeds = 0;
        int personalityDifferenceSeeds = 0;

        for (ulong seed = 1; seed <= SeedCount; seed++)
        {
            WorldPlayer elite = PlayerGenerator.GenerateAuthentic(seed, 18, BackstoryKind.EliteAcademy);
            WorldPlayer street = PlayerGenerator.GenerateAuthentic(seed, 18, BackstoryKind.StreetFootball);

            // The pre-backstory prefix of the stream (latents, ceiling and plasticity rolls, physique rolls)
            // must be bit-identical across backstories for the same seed.
            Assert.Equal(Bits(elite.Latent.Athleticism), Bits(street.Latent.Athleticism));
            Assert.Equal(Bits(elite.Latent.Technical), Bits(street.Latent.Technical));
            Assert.Equal(Bits(elite.Latent.Mental), Bits(street.Latent.Mental));
            Assert.Equal(Bits(elite.Latent.Goalkeeping), Bits(street.Latent.Goalkeeping));
            Assert.Equal(elite.BirthWeek, street.BirthWeek);
            Assert.Equal(elite.PreferredFoot, street.PreferredFoot);
            Assert.Equal(Bits(elite.WeakFoot01), Bits(street.WeakFoot01));

            foreach (AttributeId id in AttributeMetadata.All)
            {
                Assert.Equal(
                    Bits(elite.Attributes.Ceiling(id)),
                    Bits(street.Attributes.Ceiling(id)));
                Assert.Equal(
                    Bits(elite.Attributes.Plasticity(id)),
                    Bits(street.Attributes.Plasticity(id)));
            }

            // ...while the backstory does move realization, personality and visibility.
            if (AnyCurrentDiffers(elite, street)) currentDifferenceSeeds++;
            if (AnyPersonalityDiffers(elite, street)) personalityDifferenceSeeds++;
            Assert.True(
                elite.Visibility01 > street.Visibility01,
                $"seed {seed}: EliteAcademy visibility {elite.Visibility01} should exceed StreetFootball {street.Visibility01}");
        }

        Assert.True(
            currentDifferenceSeeds >= SeedCount - 1,
            $"expected the backstory to move starting Current for almost every seed, got {currentDifferenceSeeds}/{SeedCount}");
        Assert.True(
            personalityDifferenceSeeds >= SeedCount - 1,
            $"expected the backstory to move personality for almost every seed, got {personalityDifferenceSeeds}/{SeedCount}");
    }

    [Fact]
    public void CustomCeilings_AreAlsoIndependentOfBackstory()
    {
        LatentAptitudes aptitudes = new(66f, 72f, 58f, 35f);

        for (ulong seed = 1; seed <= 10; seed++)
        {
            WorldPlayer elite = PlayerGenerator.GenerateCustom(
                seed, 17, aptitudes, BackstoryKind.EliteAcademy);
            WorldPlayer street = PlayerGenerator.GenerateCustom(
                seed, 17, aptitudes, BackstoryKind.StreetFootball);

            foreach (AttributeId id in AttributeMetadata.All)
            {
                Assert.Equal(
                    Bits(elite.Attributes.Ceiling(id)),
                    Bits(street.Attributes.Ceiling(id)));
                Assert.Equal(
                    Bits(elite.Attributes.Plasticity(id)),
                    Bits(street.Attributes.Plasticity(id)));
            }
        }
    }

    // =============================================================================================
    // Gate: the age runway.
    // =============================================================================================

    [Fact]
    public void AgeSixteen_LeavesStrictlyMoreRunwayThanAgeTwentyOne()
    {
        ulong[] seeds = { 1UL, 7UL, 42UL, 1234UL, 20261127UL };
        BackstoryKind[] backstories =
        {
            BackstoryKind.EliteAcademy,
            BackstoryKind.RegionalAcademy,
            BackstoryKind.LocalClubYouth,
            BackstoryKind.StreetFootball,
            BackstoryKind.MultisportLateStarter
        };

        foreach (BackstoryKind backstory in backstories)
        {
            foreach (ulong seed in seeds)
            {
                WorldPlayer young = PlayerGenerator.GenerateAuthentic(seed, 16, backstory);
                WorldPlayer peak = PlayerGenerator.GenerateAuthentic(seed, 21, backstory);

                // Age only changes Current; the ceiling rolls are untouched by it.
                foreach (AttributeId id in AttributeMetadata.All)
                {
                    Assert.Equal(
                        Bits(young.Attributes.Ceiling(id)),
                        Bits(peak.Attributes.Ceiling(id)));
                }

                float youngSkillGap = SkillAndMindGap(young);
                float peakSkillGap = SkillAndMindGap(peak);
                Assert.True(
                    youngSkillGap > peakSkillGap,
                    $"{backstory} seed {seed}: age-16 SkillAndMind gap {youngSkillGap} should exceed age-21 gap {peakSkillGap}");

                float youngTotalGap = TotalGap(young);
                float peakTotalGap = TotalGap(peak);
                Assert.True(
                    youngTotalGap > peakTotalGap,
                    $"{backstory} seed {seed}: age-16 total gap {youngTotalGap} should exceed age-21 total gap {peakTotalGap}");
            }
        }
    }

    // =============================================================================================
    // Category behavior, goalkeeper handling and range safety.
    // =============================================================================================

    [Fact]
    public void EveryGeneratedPlayer_StaysInsideItsDeclaredRangesAndStartsNeutral()
    {
        for (ulong seed = 1; seed <= 120; seed++)
        {
            int age = 15 + (int)(seed % 24);
            BackstoryKind backstory = (BackstoryKind)(seed % 5);
            bool keeper = seed % 3 == 0;

            AssertPlayerRanges(
                PlayerGenerator.GenerateAuthentic(seed, age, backstory, keeper), age);

            if (seed <= 40)
            {
                LatentAptitudes aptitudes = new(
                    seed % 101, (seed * 7) % 101, (seed * 13) % 101, (seed * 29) % 101);
                AssertPlayerRanges(
                    PlayerGenerator.GenerateCustom(seed, age, aptitudes, backstory, keeper), age);
            }

            if (seed <= 40)
            {
                int tier = 1 + (int)(seed % 6);
                TacticalRole role = (TacticalRole)(seed % 7);
                AssertPlayerRanges(
                    PlayerGenerator.GenerateNpcForTier(seed, tier, age, role), age);
            }
        }
    }

    [Fact]
    public void GoalkeeperPreference_RaisesGoalkeepingCeilingAndCurrent()
    {
        const int Samples = 120;
        double keeperCeiling = 0.0;
        double outfieldCeiling = 0.0;
        double keeperCurrent = 0.0;
        double outfieldCurrent = 0.0;

        for (ulong seed = 1; seed <= Samples; seed++)
        {
            WorldPlayer keeper = PlayerGenerator.GenerateAuthentic(
                seed, 18, BackstoryKind.RegionalAcademy, preferGoalkeeper: true);
            WorldPlayer outfield = PlayerGenerator.GenerateAuthentic(
                seed, 18, BackstoryKind.RegionalAcademy, preferGoalkeeper: false);

            // Goalkeeping is the only aptitude the preference touches, so every other ceiling is identical.
            foreach (AttributeId id in AttributeMetadata.All)
            {
                if (id == AttributeId.Goalkeeping) continue;
                Assert.Equal(
                    Bits(keeper.Attributes.Ceiling(id)),
                    Bits(outfield.Attributes.Ceiling(id)));
            }

            keeperCeiling += keeper.Attributes.Ceiling(AttributeId.Goalkeeping);
            outfieldCeiling += outfield.Attributes.Ceiling(AttributeId.Goalkeeping);
            keeperCurrent += keeper.Attributes.Current(AttributeId.Goalkeeping);
            outfieldCurrent += outfield.Attributes.Current(AttributeId.Goalkeeping);
        }

        keeperCeiling /= Samples;
        outfieldCeiling /= Samples;
        keeperCurrent /= Samples;
        outfieldCurrent /= Samples;

        Assert.True(
            keeperCeiling > outfieldCeiling + 12.0,
            $"keeper GK ceiling mean {keeperCeiling:0.0} should clearly exceed outfield mean {outfieldCeiling:0.0}");
        Assert.True(keeperCeiling > 50.0, $"keeper GK ceiling mean {keeperCeiling:0.0} should be high");
        Assert.True(outfieldCeiling < 50.0, $"outfield GK ceiling mean {outfieldCeiling:0.0} should be low");
        Assert.True(
            keeperCurrent > outfieldCurrent + 10.0,
            $"keeper GK current mean {keeperCurrent:0.0} should clearly exceed outfield mean {outfieldCurrent:0.0}");
    }

    // =============================================================================================
    // Population pyramid and tiered NPC squads.
    // =============================================================================================

    [Fact]
    public void AuthenticCeilings_AverageNearThePopulationPyramid()
    {
        const int Samples = 400;
        double total = 0.0;

        for (ulong seed = 1; seed <= Samples; seed++)
        {
            total += MeanCeiling(
                PlayerGenerator.GenerateAuthentic(seed, 18, BackstoryKind.LocalClubYouth));
        }

        double mean = total / Samples;

        // Mixture mean is ~58: 55% amateur, 30% lower-pro, 12% top-flight, 3% star (plan §4.3).
        Assert.InRange(mean, 45.0, 70.0);
    }

    [Fact]
    public void NpcMeanCurrent_RisesStrictlyFromTierSixToTierOne()
    {
        const int PerTier = 60;
        int[] tiers = { 1, 2, 3, 4, 5, 6 };
        double[] meanCurrent = new double[tiers.Length];

        for (int index = 0; index < tiers.Length; index++)
        {
            int tier = tiers[index];
            double total = 0.0;
            for (int sample = 0; sample < PerTier; sample++)
            {
                ulong seed = (ulong)(tier * 1_000_000 + sample + 1);
                WorldPlayer npc = PlayerGenerator.GenerateNpcForTier(
                    seed, tier, 24, TacticalRole.CentralMidfielder);
                total += npc.Attributes.MeanCurrent();
            }

            meanCurrent[index] = total / PerTier;
        }

        for (int index = 0; index < tiers.Length - 1; index++)
        {
            Assert.True(
                meanCurrent[index] > meanCurrent[index + 1],
                $"tier {tiers[index]} mean current {meanCurrent[index]:0.0} should strictly exceed " +
                $"tier {tiers[index + 1]} mean current {meanCurrent[index + 1]:0.0}");
        }

        Assert.True(
            meanCurrent[0] > meanCurrent[^1] + 20.0,
            $"top-flight mean current {meanCurrent[0]:0.0} should dwarf amateur mean current {meanCurrent[^1]:0.0}");
        Assert.True(meanCurrent[0] > 60.0, $"tier-1 mean current was only {meanCurrent[0]:0.0}");
        Assert.True(meanCurrent[^1] < 45.0, $"tier-6 mean current was {meanCurrent[^1]:0.0}");
    }

    [Fact]
    public void AmateurTiers_StillContainHighCeilingGems()
    {
        const int SamplesPerTier = 500;
        int gems = 0;

        for (int tier = 5; tier <= 6; tier++)
        {
            for (int sample = 0; sample < SamplesPerTier; sample++)
            {
                ulong seed = (ulong)(tier * 10_000_000 + sample + 1);
                WorldPlayer npc = PlayerGenerator.GenerateNpcForTier(
                    seed, tier, 18, TacticalRole.Striker);
                if (MeanCeiling(npc) >= 70.0) gems++;
            }
        }

        Assert.True(
            gems >= 1,
            $"expected at least one hidden gem with mean ceiling >= 70 in tiers 5-6, found {gems}");
    }

    [Fact]
    public void NpcBackstoryWeights_FollowTheTierPyramid()
    {
        const int Samples = 300;
        int topFlightAcademies = 0;
        int amateurLocals = 0;

        for (int sample = 0; sample < Samples; sample++)
        {
            BackstoryKind top = PlayerGenerator.GenerateNpcForTier(
                (ulong)(2_000_000 + sample), 1, 18, TacticalRole.Striker).Backstory;
            if (top is BackstoryKind.EliteAcademy or BackstoryKind.RegionalAcademy) topFlightAcademies++;

            BackstoryKind bottom = PlayerGenerator.GenerateNpcForTier(
                (ulong)(3_000_000 + sample), 6, 18, TacticalRole.Striker).Backstory;
            if (bottom is BackstoryKind.LocalClubYouth or BackstoryKind.StreetFootball) amateurLocals++;
        }

        // Fixed weights: tier 1 expects 85% academy products, tier 6 expects 75% local/street footballers.
        Assert.True(
            topFlightAcademies >= Samples * 7 / 10,
            $"tier 1 should be mostly academy products, got {topFlightAcademies}/{Samples}");
        Assert.True(
            amateurLocals >= Samples * 6 / 10,
            $"tier 6 should be mostly local/street footballers, got {amateurLocals}/{Samples}");
    }

    [Fact]
    public void NpcGoalkeeperRole_GetsHighGoalkeepingCeiling()
    {
        const int Samples = 120;
        double keeperCeiling = 0.0;
        double defenderCeiling = 0.0;

        for (ulong seed = 1; seed <= Samples; seed++)
        {
            keeperCeiling += PlayerGenerator
                .GenerateNpcForTier(seed, 3, 22, TacticalRole.Goalkeeper)
                .Attributes.Ceiling(AttributeId.Goalkeeping);
            defenderCeiling += PlayerGenerator
                .GenerateNpcForTier(seed, 3, 22, TacticalRole.CenterBack)
                .Attributes.Ceiling(AttributeId.Goalkeeping);
        }

        keeperCeiling /= Samples;
        defenderCeiling /= Samples;

        Assert.True(
            keeperCeiling > defenderCeiling + 15.0,
            $"keeper-role GK ceiling mean {keeperCeiling:0.0} should clearly exceed centre-back mean {defenderCeiling:0.0}");
    }

    [Fact]
    public void NpcGeneration_IsDeterministic()
    {
        for (int tier = 1; tier <= 6; tier++)
        {
            WorldPlayer first = PlayerGenerator.GenerateNpcForTier(
                4242UL, tier, 20, TacticalRole.Winger);
            WorldPlayer second = PlayerGenerator.GenerateNpcForTier(
                4242UL, tier, 20, TacticalRole.Winger);
            AssertSameGeneratedValues(first, second);
        }
    }

    // =============================================================================================
    // Custom mode.
    // =============================================================================================

    [Fact]
    public void CustomHigherAptitudes_RaiseTheMatchingAttributeFamilyCeilings()
    {
        const int Samples = 50;
        double athleticTopSpeedHigh = 0.0;
        double athleticTopSpeedLow = 0.0;
        double technicalPassingHigh = 0.0;
        double technicalPassingLow = 0.0;

        for (ulong seed = 1; seed <= Samples; seed++)
        {
            WorldPlayer speedster = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(80f, 50f, 50f, 30f), BackstoryKind.LocalClubYouth);
            WorldPlayer plodder = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(30f, 50f, 50f, 30f), BackstoryKind.LocalClubYouth);
            athleticTopSpeedHigh += speedster.Attributes.Ceiling(AttributeId.TopSpeed);
            athleticTopSpeedLow += plodder.Attributes.Ceiling(AttributeId.TopSpeed);

            WorldPlayer playmaker = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(50f, 80f, 50f, 30f), BackstoryKind.LocalClubYouth);
            WorldPlayer honest = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(50f, 30f, 50f, 30f), BackstoryKind.LocalClubYouth);
            technicalPassingHigh += playmaker.Attributes.Ceiling(AttributeId.Passing);
            technicalPassingLow += honest.Attributes.Ceiling(AttributeId.Passing);
        }

        athleticTopSpeedHigh /= Samples;
        athleticTopSpeedLow /= Samples;
        technicalPassingHigh /= Samples;
        technicalPassingLow /= Samples;

        Assert.True(
            athleticTopSpeedHigh > athleticTopSpeedLow + 20.0,
            $"Athleticism 80 TopSpeed ceiling mean {athleticTopSpeedHigh:0.0} should exceed Athleticism 30 mean {athleticTopSpeedLow:0.0}");
        Assert.True(
            technicalPassingHigh > technicalPassingLow + 20.0,
            $"Technical 80 Passing ceiling mean {technicalPassingHigh:0.0} should exceed Technical 30 mean {technicalPassingLow:0.0}");
    }

    [Fact]
    public void CustomGeneration_IsDeterministic_UsesSuppliedAptitudes_AndSkipsTalentDraws()
    {
        LatentAptitudes aptitudes = new(71f, 63f, 55f, 40f);
        WorldPlayer first = PlayerGenerator.GenerateCustom(
            1234UL, 17, aptitudes, BackstoryKind.RegionalAcademy);
        WorldPlayer second = PlayerGenerator.GenerateCustom(
            1234UL, 17, aptitudes, BackstoryKind.RegionalAcademy);
        AssertSameGeneratedValues(first, second);

        Assert.Equal(71f, first.Latent.Athleticism);
        Assert.Equal(63f, first.Latent.Technical);
        Assert.Equal(55f, first.Latent.Mental);
        Assert.Equal(40f, first.Latent.Goalkeeping);

        WorldPlayer clamped = PlayerGenerator.GenerateCustom(
            1234UL, 17, new LatentAptitudes(150f, -20f, 50f, 999f), BackstoryKind.RegionalAcademy);
        Assert.Equal(100f, clamped.Latent.Athleticism);
        Assert.Equal(0f, clamped.Latent.Technical);
        Assert.Equal(50f, clamped.Latent.Mental);
        Assert.Equal(100f, clamped.Latent.Goalkeeping);

        // Custom mode consumes no anchor/aptitude draws, so the same seed uses the same ceiling noise:
        // an Athleticism-only change moves TopSpeed by exactly weight * delta when nothing clamps.
        int comparableSeeds = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            float highSpeed = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(80f, 50f, 50f, 30f), BackstoryKind.LocalClubYouth)
                .Attributes.Ceiling(AttributeId.TopSpeed);
            float lowSpeed = PlayerGenerator.GenerateCustom(
                seed, 18, new LatentAptitudes(30f, 50f, 50f, 30f), BackstoryKind.LocalClubYouth)
                .Attributes.Ceiling(AttributeId.TopSpeed);

            if (highSpeed is > 20.5f and < 98.5f && lowSpeed is > 20.5f and < 98.5f)
            {
                Assert.True(
                    MathF.Abs(highSpeed - lowSpeed - 0.85f * 50f) < 0.001f,
                    $"seed {seed}: expected a 42.5 point TopSpeed ceiling shift from +50 Athleticism, " +
                    $"got {highSpeed - lowSpeed}");
                comparableSeeds++;
            }
        }

        Assert.True(
            comparableSeeds >= 30,
            $"expected almost every seed to stay clear of the ceiling clamps, only {comparableSeeds}/40 did");
    }

    // =============================================================================================
    // Validation.
    // =============================================================================================

    [Fact]
    public void AgeOutsideSupportedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateAuthentic(1UL, 14, BackstoryKind.LocalClubYouth));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateAuthentic(1UL, 39, BackstoryKind.LocalClubYouth));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateCustom(1UL, 14, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateCustom(1UL, 39, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateNpcForTier(1UL, 1, 14, TacticalRole.Striker));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateNpcForTier(1UL, 1, 39, TacticalRole.Striker));

        // The bounds themselves are valid.
        Assert.Equal(15, PlayerGenerator.GenerateAuthentic(1UL, 15, BackstoryKind.LocalClubYouth).Age);
        Assert.Equal(38, PlayerGenerator.GenerateAuthentic(1UL, 38, BackstoryKind.LocalClubYouth).Age);
    }

    [Fact]
    public void TierOutsideSupportedRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateNpcForTier(1UL, 0, 20, TacticalRole.Striker));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateNpcForTier(1UL, 7, 20, TacticalRole.Striker));

        Assert.NotNull(PlayerGenerator.GenerateNpcForTier(1UL, 1, 20, TacticalRole.Striker));
        Assert.NotNull(PlayerGenerator.GenerateNpcForTier(1UL, 6, 20, TacticalRole.Striker));
    }

    [Fact]
    public void UndefinedBackstory_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateAuthentic(1UL, 18, (BackstoryKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateAuthentic(1UL, 18, (BackstoryKind)(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PlayerGenerator.GenerateCustom(1UL, 18, LatentAptitudes.Neutral, (BackstoryKind)5));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>Compares every generated value bit-for-bit; identity (id and names) is deliberately excluded.</summary>
    private static void AssertSameGeneratedValues(WorldPlayer expected, WorldPlayer actual)
    {
        Assert.Equal(expected.Age, actual.Age);
        Assert.Equal(expected.BirthWeek, actual.BirthWeek);
        Assert.Equal(expected.PreferredFoot, actual.PreferredFoot);
        Assert.Equal(expected.Backstory, actual.Backstory);

        Assert.Equal(Bits(expected.WeakFoot01), Bits(actual.WeakFoot01));

        Assert.Equal(Bits(expected.Latent.Athleticism), Bits(actual.Latent.Athleticism));
        Assert.Equal(Bits(expected.Latent.Technical), Bits(actual.Latent.Technical));
        Assert.Equal(Bits(expected.Latent.Mental), Bits(actual.Latent.Mental));
        Assert.Equal(Bits(expected.Latent.Goalkeeping), Bits(actual.Latent.Goalkeeping));

        foreach (AttributeId id in AttributeMetadata.All)
        {
            Assert.Equal(
                Bits(expected.Attributes.Current(id)),
                Bits(actual.Attributes.Current(id)));
            Assert.Equal(
                Bits(expected.Attributes.Ceiling(id)),
                Bits(actual.Attributes.Ceiling(id)));
            Assert.Equal(
                Bits(expected.Attributes.Plasticity(id)),
                Bits(actual.Attributes.Plasticity(id)));
        }

        Assert.Equal(Bits(expected.Personality.Resilience), Bits(actual.Personality.Resilience));
        Assert.Equal(Bits(expected.Personality.WorkEthic), Bits(actual.Personality.WorkEthic));
        Assert.Equal(Bits(expected.Personality.Creativity), Bits(actual.Personality.Creativity));
        Assert.Equal(Bits(expected.Personality.Discipline), Bits(actual.Personality.Discipline));

        Assert.Equal(Bits(expected.Visibility01), Bits(actual.Visibility01));
        Assert.Equal(Bits(expected.Condition01), Bits(actual.Condition01));
        Assert.Equal(Bits(expected.MatchSharpness01), Bits(actual.MatchSharpness01));
        Assert.Equal(Bits(expected.Form01), Bits(actual.Form01));
        Assert.Equal(expected.TrainingFocus, actual.TrainingFocus);
        Assert.Equal(expected.Injury.Kind, actual.Injury.Kind);
        Assert.Equal(expected.Injury.Severity, actual.Injury.Severity);
        Assert.Equal(expected.Injury.WeeksOutRemaining, actual.Injury.WeeksOutRemaining);
        Assert.Equal(expected.ClubId, actual.ClubId);
        Assert.Equal(expected.SuspensionWeeksRemaining, actual.SuspensionWeeksRemaining);
        Assert.Equal(expected.IsRetired, actual.IsRetired);
        Assert.Equal(expected.CurrentSeason.Season, actual.CurrentSeason.Season);
        Assert.Equal(expected.CurrentSeason.Appearances, actual.CurrentSeason.Appearances);
        Assert.Equal(expected.CurrentSeason.MinutesPlayed, actual.CurrentSeason.MinutesPlayed);
    }

    /// <summary>Asserts every rolled value sits inside the range the plan and the metadata table declare.</summary>
    private static void AssertPlayerRanges(WorldPlayer player, int expectedAge)
    {
        Assert.Equal(expectedAge, player.Age);
        Assert.InRange(player.BirthWeek, 1, 52);
        Assert.True(
            player.WeakFoot01 is >= 0f and <= 1f,
            $"weak foot out of range: {player.WeakFoot01}");

        foreach (AttributeId id in AttributeMetadata.All)
        {
            AttributeDefinition definition = AttributeMetadata.Get(id);
            PlayerAttributeState state = player.Attributes[id];

            Assert.True(
                state.Ceiling is >= 20f and <= 99f,
                $"{id} ceiling out of [20,99]: {state.Ceiling}");
            Assert.True(
                state.Current is >= 15f and <= 99f,
                $"{id} current out of [15,99]: {state.Current}");
            Assert.True(
                state.Current <= state.Ceiling,
                $"{id} current {state.Current} exceeds ceiling {state.Ceiling}");
            Assert.True(
                state.Plasticity >= definition.PlasticityMin && state.Plasticity <= definition.PlasticityMax,
                $"{id} plasticity {state.Plasticity} outside [{definition.PlasticityMin},{definition.PlasticityMax}]");
            Assert.True(state.IsValid, $"{id} state is not valid: {state}");
        }

        Assert.True(
            player.Personality.Resilience is >= 0f and <= 100f &&
            player.Personality.WorkEthic is >= 0f and <= 100f &&
            player.Personality.Creativity is >= 0f and <= 100f &&
            player.Personality.Discipline is >= 0f and <= 100f,
            $"personality out of [0,100]: {player.Personality}");

        Assert.InRange(player.Visibility01, 0f, 1f);
        Assert.InRange(player.Condition01, 0f, 1f);
        Assert.InRange(player.MatchSharpness01, 0.55f, 0.70f);
        Assert.InRange(player.Form01, 0f, 1f);

        // Fresh-player defaults (plan W1 step 10).
        Assert.Equal(1f, player.Condition01);
        Assert.Equal(0.5f, player.Form01);
        Assert.Equal(TrainingFocus.Balanced, player.TrainingFocus);
        Assert.False(player.Injury.IsInjured);
        Assert.Equal(InjuryKind.None, player.Injury.Kind);
        Assert.Equal(ClubId.None, player.ClubId);
        Assert.True(player.IsFreeAgent);
        Assert.False(player.IsRetired);
        Assert.Equal(0, player.CurrentSeason.Season);
        Assert.Equal(0, player.CurrentSeason.Appearances);
        Assert.Empty(player.SeasonHistory);
    }

    private static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

    private static bool AnyCurrentDiffers(WorldPlayer first, WorldPlayer second)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (Bits(first.Attributes.Current(id)) != Bits(second.Attributes.Current(id))) return true;
        }

        return false;
    }

    private static bool AnyPersonalityDiffers(WorldPlayer first, WorldPlayer second) =>
        Bits(first.Personality.Resilience) != Bits(second.Personality.Resilience) ||
        Bits(first.Personality.WorkEthic) != Bits(second.Personality.WorkEthic) ||
        Bits(first.Personality.Creativity) != Bits(second.Personality.Creativity) ||
        Bits(first.Personality.Discipline) != Bits(second.Personality.Discipline);

    private static double MeanCeiling(WorldPlayer player)
    {
        double sum = 0.0;
        foreach (AttributeId id in AttributeMetadata.All) sum += player.Attributes.Ceiling(id);
        return sum / AttributeCount;
    }

    private static float TotalGap(WorldPlayer player)
    {
        float sum = 0f;
        foreach (AttributeId id in AttributeMetadata.All) sum += player.Attributes.Gap(id);
        return sum;
    }

    private static float SkillAndMindGap(WorldPlayer player)
    {
        float sum = 0f;
        foreach (AttributeId id in AttributeMetadata.OfCategory(AttributeCategory.SkillAndMind))
        {
            sum += player.Attributes.Gap(id);
        }

        return sum;
    }
}
