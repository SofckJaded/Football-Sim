using System;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Career;

public sealed class CareerConfigTests
{
    [Fact]
    public void Defaults_AreValidOnceNamed()
    {
        CareerConfig config = Named();

        Assert.Equal(20261127UL, config.WorldSeed);
        Assert.Equal(CareerCreationMode.Authentic, config.Mode);
        Assert.Equal(17, config.StartingAge);
        Assert.Equal(BackstoryKind.LocalClubYouth, config.Backstory);
        Assert.Null(config.PreferredFoot);
        Assert.False(config.PreferGoalkeeper);
        Assert.Null(config.CustomAptitudes);
        Assert.Null(config.Potential);
        Assert.Equal(ClubId.None, config.StartingClubId);
        Assert.Equal(TrainingFocus.Balanced, config.InitialTrainingFocus);
        Assert.False(config.ShowHiddenPotential);

        config.Validate();
        (config with { PreferredFoot = Footedness.Left }).Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsMissingName(string name)
    {
        Assert.Throws<ArgumentException>(new CareerConfig { FirstName = name, LastName = name }.Validate);
        Assert.Throws<ArgumentException>(new CareerConfig().Validate);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(23)]
    public void Validate_RejectsAgeOutsideSupportedRange(int age)
    {
        Assert.Throws<ArgumentOutOfRangeException>((Named() with { StartingAge = age }).Validate);
    }

    [Fact]
    public void Validate_AcceptsAgeBounds()
    {
        (Named() with { StartingAge = 16 }).Validate();
        (Named() with { StartingAge = 22 }).Validate();
    }

    [Fact]
    public void Validate_RejectsUndefinedEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (Named() with { Mode = (CareerCreationMode)2 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Named() with { Backstory = (BackstoryKind)5 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => (Named() with { InitialTrainingFocus = (TrainingFocus)10 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Named() with { Potential = (PotentialPreset)5 }).Validate());
    }

    [Theory]
    [InlineData(-0.5, 50.0, 50.0, 50.0)]
    [InlineData(50.0, 100.5, 50.0, 50.0)]
    [InlineData(50.0, 50.0, 50.0, -1.0)]
    public void Validate_RejectsOutOfRangeCustomAptitudes(double a, double t, double m, double g)
    {
        CareerConfig config = Named() with
        {
            CustomAptitudes = new LatentAptitudes((float)a, (float)t, (float)m, (float)g)
        };
        Assert.Throws<ArgumentOutOfRangeException>(config.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Validate_RejectsNonFiniteCustomAptitudes(int slot)
    {
        LatentAptitudes aptitudes = slot switch
        {
            0 => new LatentAptitudes(float.NaN, 50f, 50f, 50f),
            1 => new LatentAptitudes(50f, float.PositiveInfinity, 50f, 50f),
            2 => new LatentAptitudes(50f, 50f, float.NegativeInfinity, 50f),
            _ => new LatentAptitudes(50f, 50f, 50f, float.NaN)
        };
        Assert.Throws<ArgumentOutOfRangeException>((Named() with { CustomAptitudes = aptitudes }).Validate);
    }

    [Fact]
    public void DisplayName_ComposesFromPartsAndTrims()
    {
        Assert.Equal("Ada Lovelace", new CareerConfig { FirstName = "Ada", LastName = "Lovelace" }.DisplayName);
        Assert.Equal("Ada", new CareerConfig { FirstName = "Ada", LastName = string.Empty }.DisplayName);
        Assert.Equal("Lovelace", new CareerConfig { FirstName = string.Empty, LastName = "Lovelace" }.DisplayName);
        Assert.Equal("Ada Lovelace", new CareerConfig { FirstName = "  Ada  ", LastName = "  Lovelace " }.DisplayName);
        Assert.Equal("Ada", new CareerConfig { FirstName = " Ada ", LastName = "   " }.DisplayName);
        Assert.Equal(string.Empty, new CareerConfig().DisplayName);
    }

    [Fact]
    public void ResolveAptitudes_CustomSlidersWinAndAreClamped()
    {
        CareerConfig chosen = new()
        {
            CustomAptitudes = new LatentAptitudes(11f, 22f, 33f, 44f),
            Potential = PotentialPreset.Generational
        };
        Assert.Equal(new LatentAptitudes(11f, 22f, 33f, 44f), chosen.ResolveAptitudes());

        CareerConfig clamped = new() { CustomAptitudes = new LatentAptitudes(150f, -20f, 50f, 999f) };
        Assert.Equal(new LatentAptitudes(100f, 0f, 50f, 100f), clamped.ResolveAptitudes());
    }

    [Fact]
    public void ResolveAptitudes_PresetsIncreaseStrictly()
    {
        LatentAptitudes previous = default;
        bool hasPrevious = false;

        foreach (PotentialPreset preset in Enum.GetValues<PotentialPreset>())
        {
            LatentAptitudes aptitudes = new CareerConfig { Potential = preset }.ResolveAptitudes();
            Assert.Equal(20f, aptitudes.Goalkeeping);
            if (hasPrevious)
            {
                Assert.True(aptitudes.Athleticism > previous.Athleticism, $"{preset}: Athleticism must rise");
                Assert.True(aptitudes.Technical > previous.Technical, $"{preset}: Technical must rise");
                Assert.True(aptitudes.Mental > previous.Mental, $"{preset}: Mental must rise");
                Assert.True(aptitudes.OutfieldMean > previous.OutfieldMean, $"{preset}: outfield mean must rise");
            }

            previous = aptitudes;
            hasPrevious = true;
        }
    }

    [Fact]
    public void ResolveAptitudes_NoChoiceDefaultsToRegionalStandout()
    {
        LatentAptitudes fallback = new CareerConfig().ResolveAptitudes();
        Assert.Equal(new LatentAptitudes(54f, 52f, 55f, 20f), fallback);
        Assert.Equal(new CareerConfig { Potential = PotentialPreset.RegionalStandout }.ResolveAptitudes(), fallback);
    }

    [Fact]
    public void ResolveAptitudes_PreferGoalkeeperRaisesHandsAboveTheOutfieldValue()
    {
        foreach (PotentialPreset preset in Enum.GetValues<PotentialPreset>())
        {
            LatentAptitudes outfield = new CareerConfig { Potential = preset }.ResolveAptitudes();
            LatentAptitudes keeper = new CareerConfig
            {
                Potential = preset,
                PreferGoalkeeper = true
            }.ResolveAptitudes();

            Assert.Equal(outfield.Athleticism, keeper.Athleticism);
            Assert.Equal(outfield.Technical, keeper.Technical);
            Assert.Equal(outfield.Mental, keeper.Mental);
            Assert.Equal(20f, outfield.Goalkeeping);
            Assert.Equal(Math.Clamp(keeper.OutfieldMean + 8f, 5f, 98f), keeper.Goalkeeping);
            Assert.True(
                keeper.Goalkeeping > keeper.OutfieldMean,
                $"{preset}: keeper hands {keeper.Goalkeeping} should exceed outfield mean {keeper.OutfieldMean}");
        }
    }

    /// <summary>
    /// Plan §4.4 invariant driven through <see cref="CareerConfig"/> exactly as the session drives it:
    /// switching only the backstory keeps every ceiling bit-for-bit identical while starting Current moves.
    /// </summary>
    [Fact]
    public void AuthenticGenerator_KeepsCeilingsBackstoryIndependent()
    {
        Assert.Equal(18, AttributeMetadata.Count);

        foreach (ulong seed in new[] { 1UL, 7UL, 42UL, 20261127UL })
        {
            foreach (int age in new[] { 16, 18, 22 })
            {
                CareerConfig eliteConfig = new()
                {
                    WorldSeed = seed,
                    Mode = CareerCreationMode.Authentic,
                    FirstName = "Ada",
                    LastName = "Valdis",
                    StartingAge = age,
                    Backstory = BackstoryKind.EliteAcademy
                };
                CareerConfig streetConfig = eliteConfig with { Backstory = BackstoryKind.StreetFootball };

                eliteConfig.Validate();
                streetConfig.Validate();

                // Authentic mode rolls its own aptitudes; ResolveAptitudes never reads the backstory.
                Assert.Equal(eliteConfig.ResolveAptitudes(), streetConfig.ResolveAptitudes());

                WorldPlayer elite = Generate(eliteConfig);
                WorldPlayer street = Generate(streetConfig);

                foreach (AttributeId id in AttributeMetadata.All)
                {
                    Assert.Equal(
                        BitConverter.SingleToInt32Bits(elite.Attributes.Ceiling(id)),
                        BitConverter.SingleToInt32Bits(street.Attributes.Ceiling(id)));
                }

                Assert.True(
                    AnyCurrentDiffers(elite, street),
                    $"seed {seed}, age {age}: the backstory must move starting Current");
                Assert.Equal(BackstoryKind.EliteAcademy, elite.Backstory);
                Assert.Equal(BackstoryKind.StreetFootball, street.Backstory);
            }
        }
    }

    private static CareerConfig Named() => new() { FirstName = "Ada", LastName = "Lovelace" };

    private static WorldPlayer Generate(CareerConfig config) => PlayerGenerator.GenerateAuthentic(
        config.WorldSeed,
        config.StartingAge,
        config.Backstory,
        config.PreferGoalkeeper,
        firstName: config.FirstName,
        lastName: config.LastName,
        preferredFootOverride: config.PreferredFoot);

    private static bool AnyCurrentDiffers(WorldPlayer first, WorldPlayer second)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            if (BitConverter.SingleToInt32Bits(first.Attributes.Current(id)) !=
                BitConverter.SingleToInt32Bits(second.Attributes.Current(id)))
            {
                return true;
            }
        }

        return false;
    }
}
