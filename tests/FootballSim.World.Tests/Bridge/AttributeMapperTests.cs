using System;
using FootballSim.Core;
using FootballSim.World.Bridge;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Bridge;

/// <summary>
/// W5 attribute-mapper contract (<c>docs/WORLD_CAREER_PLAN.md</c> §4.2): generated players map to
/// validation-clean engine values, the mapping is monotonic, the plan's ranges hold at 1, 15 and 99 for all
/// eighteen attributes, the strong foot survives, and <see cref="MatchPlayerSpec"/> carries a clamped stamina.
/// </summary>
public sealed class AttributeMapperTests
{
    private static readonly TacticalRole[] Roles =
    {
        TacticalRole.Goalkeeper,
        TacticalRole.CenterBack,
        TacticalRole.FullBack,
        TacticalRole.DefensiveMidfielder,
        TacticalRole.CentralMidfielder,
        TacticalRole.Winger,
        TacticalRole.Striker
    };

    [Fact]
    public void GeneratedNpcPlayers_MapToValidationCleanEngineValues()
    {
        for (ulong seed = 1; seed <= 28; seed++)
        {
            int tier = 1 + (int)(seed % 6);
            TacticalRole role = Roles[(int)(seed % (ulong)Roles.Length)];
            WorldPlayer player = PlayerGenerator.GenerateNpcForTier(seed, tier, 15 + (int)(seed % 24), role);
            AssertMappingValid(player, role);
        }
    }

    [Fact]
    public void AuthenticAndCustomPlayers_MapToValidationCleanEngineValues()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            BackstoryKind backstory = (BackstoryKind)(seed % 5);
            WorldPlayer authentic = PlayerGenerator.GenerateAuthentic(
                seed, 16 + (int)(seed % 7), backstory, seed % 2 == 0);
            WorldPlayer custom = PlayerGenerator.GenerateCustom(
                seed + 100UL, 18, new LatentAptitudes(55f + seed, 45f, 60f - seed, 30f), backstory);

            AssertMappingValid(authentic, TacticalRole.CentralMidfielder);
            AssertMappingValid(custom, TacticalRole.Goalkeeper);
        }
    }

    [Fact]
    public void RaisingOneWorldAttribute_NeverMakesItsMappedFieldWorse()
    {
        WorldPlayer player = PlayerGenerator.GenerateCustom(
            9UL, 20, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCeiling(id, AttributeMetadata.MaximumCeiling);
        }

        foreach (AttributeId id in AttributeMetadata.All)
        {
            SetEveryCurrent(player, 50f);
            player.Attributes.SetCurrent(id, 15f);
            PlayerAttributes lowAttributes = AttributeMapper.ToAttributes(player);
            PlayerTendencies lowTendencies = AttributeMapper.ToTendencies(player, TacticalRole.Striker, PlayerArchetype.None);

            player.Attributes.SetCurrent(id, 85f);
            PlayerAttributes highAttributes = AttributeMapper.ToAttributes(player);
            PlayerTendencies highTendencies = AttributeMapper.ToTendencies(player, TacticalRole.Striker, PlayerArchetype.None);

            AssertMovedInTheRightDirection(id, lowAttributes, highAttributes, lowTendencies, highTendencies);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(15f)]
    [InlineData(99f)]
    public void ExtremeWorldValues_MapToPlanRangesAndValidate(float value)
    {
        WorldPlayer player = PlayerGenerator.GenerateCustom(
            3UL, 19, LatentAptitudes.Neutral, BackstoryKind.RegionalAcademy);
        SetEveryCurrent(player, value);

        PlayerAttributes mapped = AttributeMapper.ToAttributes(player);
        SimulationConfigValidation.ValidatePlayerAttributes(mapped);
        SimulationConfigValidation.ValidateTendencies(
            AttributeMapper.ToTendencies(player, TacticalRole.CenterBack, PlayerArchetype.BallWinner));
        Assert.InRange(mapped.MaxSprintSpeed, 6.6f, 9.6f);
        Assert.InRange(mapped.Acceleration, 5.0f, 8.2f);
        Assert.InRange(mapped.ReachRadius, 0.68f, 0.84f);
        Assert.InRange(mapped.Strength01, 0.45f, 0.96f);
        Assert.InRange(mapped.StaminaCapacity, 0.72f, 1.25f);
        Assert.InRange(mapped.AgilityTurnRate, 7.2f, 11.2f);
        Assert.InRange(mapped.Balance01, 0.48f, 0.96f);
        Assert.InRange(mapped.FootAccuracy01, 0.48f, 0.96f);
        Assert.InRange(mapped.ShotPowerMax, 24.0f, 35.5f);
        Assert.InRange(mapped.FirstTouch01, 0.48f, 0.96f);
        Assert.InRange(mapped.Composure01, 0.45f, 0.96f);
        Assert.InRange(mapped.ReactionDelaySeconds, 0.04f, 0.16f);
        Assert.InRange(mapped.GoalkeepingReflex01, 0.45f, 0.96f);
    }

    [Fact]
    public void OutOfRangeCurrentValues_ClampToThePlanEndpoints()
    {
        WorldPlayer player = PlayerGenerator.GenerateCustom(
            4UL, 20, LatentAptitudes.Neutral, BackstoryKind.LocalClubYouth);

        SetEveryCurrent(player, -50f);
        PlayerAttributes floor = AttributeMapper.ToAttributes(player);
        Assert.Equal(6.6f, floor.MaxSprintSpeed, 4);
        Assert.Equal(0.16f, floor.ReactionDelaySeconds, 4);

        SetEveryCurrent(player, 500f);
        PlayerAttributes ceiling = AttributeMapper.ToAttributes(player);
        Assert.Equal(9.6f, ceiling.MaxSprintSpeed, 4);
        Assert.Equal(0.04f, ceiling.ReactionDelaySeconds, 4);
    }

    [Fact]
    public void PreferredFoot_IsPreservedForBothFeet()
    {
        foreach (Footedness foot in new[] { Footedness.Right, Footedness.Left })
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                21UL, 20, BackstoryKind.EliteAcademy, false, new PlayerId(7), "Foot", "Test", foot);
            Assert.Equal(foot, player.PreferredFoot);
            Assert.Equal(foot, AttributeMapper.ToAttributes(player).PreferredFoot);
        }
    }

    [Fact]
    public void MatchPlayerSpec_CarriesIdentityRoleArchetypeAndClampedStamina()
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(
            33UL, 22, BackstoryKind.EliteAcademy, false, new PlayerId(4), "Ada", "Valdis");

        MatchPlayerSpec spec = AttributeMapper.ToMatchPlayerSpec(
            player, TacticalRole.Goalkeeper, PlayerArchetype.SweeperKeeper, 2.5f);

        Assert.Equal("Ada Valdis", spec.Name);
        Assert.Equal(TacticalRole.Goalkeeper, spec.Role);
        Assert.Equal(PlayerArchetype.SweeperKeeper, spec.Archetype);
        Assert.Equal(1f, spec.InitialStamina01);
        Assert.Equal(player.PreferredFoot, spec.PreferredFoot);
        Assert.NotNull(spec.Attributes);
        Assert.NotNull(spec.Tendencies);
        SimulationConfigValidation.ValidatePlayerAttributes(spec.Attributes);
        SimulationConfigValidation.ValidateTendencies(spec.Tendencies!);
        Assert.Equal(AttributeMapper.ToAttributes(player).MaxSprintSpeed, spec.Attributes!.MaxSprintSpeed);

        Assert.Equal(0f, RateStamina(player, -3f));
        Assert.Equal(0.5f, RateStamina(player, 0.5f));
        Assert.Equal(1f, RateStamina(player, 1f));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    private static void AssertMappingValid(WorldPlayer player, TacticalRole role)
    {
        PlayerAttributes attributes = AttributeMapper.ToAttributes(player);
        SimulationConfigValidation.ValidatePlayerAttributes(attributes, $"{player.DisplayName} attributes");

        foreach (PlayerArchetype archetype in Enum.GetValues<PlayerArchetype>())
        {
            PlayerTendencies tendencies = AttributeMapper.ToTendencies(player, role, archetype);
            SimulationConfigValidation.ValidateTendencies(tendencies, $"{player.DisplayName} tendencies");
        }
    }

    private static void SetEveryCurrent(WorldPlayer player, float current)
    {
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCeiling(id, AttributeMetadata.MaximumCeiling);
            player.Attributes.SetCurrent(id, current);
        }
    }

    private static float RateStamina(WorldPlayer player, float stamina01) =>
        AttributeMapper.ToMatchPlayerSpec(player, TacticalRole.Striker, PlayerArchetype.None, stamina01)
            .InitialStamina01;

    /// <summary>The engine attribute a world attribute maps to directly, or null when it only moves tendencies.</summary>
    private static float? DirectField(PlayerAttributes attributes, AttributeId id) => id switch
    {
        AttributeId.TopSpeed => attributes.MaxSprintSpeed,
        AttributeId.Acceleration => attributes.Acceleration,
        AttributeId.NaturalReach => attributes.ReachRadius,
        AttributeId.Strength => attributes.Strength01,
        AttributeId.Stamina => attributes.StaminaCapacity,
        AttributeId.Agility => attributes.AgilityTurnRate,
        AttributeId.Balance => attributes.Balance01,
        AttributeId.Passing => attributes.FootAccuracy01,
        AttributeId.Finishing => attributes.FootAccuracy01,
        AttributeId.ShotPower => attributes.ShotPowerMax,
        AttributeId.FirstTouch => attributes.FirstTouch01,
        AttributeId.Composure => attributes.Composure01,
        AttributeId.Goalkeeping => attributes.GoalkeepingReflex01,
        _ => null
    };

    private static void AssertMovedInTheRightDirection(
        AttributeId id,
        PlayerAttributes low,
        PlayerAttributes high,
        PlayerTendencies lowTendencies,
        PlayerTendencies highTendencies)
    {
        if (DirectField(low, id) is float lowValue)
        {
            Assert.True(DirectField(high, id)!.Value >= lowValue, $"{id} must not lower its direct field");
        }

        switch (id)
        {
            case AttributeId.Dribbling:
                Assert.True(highTendencies.DribblePreference01 >= lowTendencies.DribblePreference01);
                break;
            case AttributeId.Tackling:
                Assert.True(highTendencies.PressingAggression01 >= lowTendencies.PressingAggression01);
                break;
            case AttributeId.Vision:
                Assert.True(highTendencies.DirectPassPreference01 >= lowTendencies.DirectPassPreference01);
                Assert.True(highTendencies.ScanAppetite01 >= lowTendencies.ScanAppetite01);
                break;
            case AttributeId.Composure:
            case AttributeId.Positioning:
            case AttributeId.OffTheBall:
                // Reaction delay is a cost, so a better reader reacts sooner; the field must not rise.
                Assert.True(high.ReactionDelaySeconds <= low.ReactionDelaySeconds);
                if (id == AttributeId.Positioning)
                {
                    Assert.True(
                        highTendencies.PositionalDiscipline01 >= lowTendencies.PositionalDiscipline01);
                }

                break;
            case AttributeId.Goalkeeping:
                Assert.True(highTendencies.SweepAggression01 >= lowTendencies.SweepAggression01);
                break;
        }
    }
}
