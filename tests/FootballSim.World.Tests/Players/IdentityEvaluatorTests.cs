using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Players;
using Xunit;

namespace FootballSim.World.Tests.Players;

/// <summary>
/// W1 emergent-identity contract (plan §4.7): the transparent role weight table, role selection with its
/// tie-break, the six-point/eighty-five-percent secondary window, the fixed style-tag rule order, the
/// archetype/tendency bridge to the match engine and structural determinism.
///
/// Scenario players are generated with <see cref="PlayerGenerator.GenerateAuthentic"/> and then given fixed
/// current values, so every expectation below is a pure function of the attribute vector.
/// </summary>
public sealed class IdentityEvaluatorTests
{
    /// <summary>The complete style-tag vocabulary the plan fixes; every produced tag must be one of these.</summary>
    private static readonly string[] KnownStyleTags =
    {
        "Speed Demon",
        "Deep-Lying Playmaker",
        "Poacher",
        "Box-to-Box Engine",
        "Target Forward",
        "Technical Dribbler",
        "Ball-Winning Destroyer",
        "Composed Ball-Playing CB",
        "Sweeper Keeper",
        "Raw Amateur Prospect",
        "Balanced Profile",
        "Physical Presence"
    };

    // =============================================================================================
    // The weight table.
    // =============================================================================================

    [Fact]
    public void RoleSuitabilityWeights_EveryRoleRowSumsToOne()
    {
        Assert.Equal(8, RoleSuitabilityWeights.All.Count);
        var covered = new HashSet<WorldRole>();

        foreach ((WorldRole role, IReadOnlyDictionary<AttributeId, float> weights) in RoleSuitabilityWeights.All)
        {
            Assert.True(Enum.IsDefined(role), $"{role} is not a defined world role.");
            Assert.True(covered.Add(role), $"{role} appears more than once in the weight table.");
            Assert.Same(weights, RoleSuitabilityWeights.For(role));
            Assert.True(weights.Count > 0, $"{role} has no weighted attributes.");

            float sum = 0f;
            foreach (KeyValuePair<AttributeId, float> entry in weights)
            {
                Assert.True(Enum.IsDefined(entry.Key), $"{role} references an undefined attribute.");
                Assert.InRange(entry.Value, 0.01f, 1f);
                sum += entry.Value;
            }

            // Each row is an average, so its weights must sum to exactly one (within float rounding).
            Assert.Equal(1f, sum, 3);
        }

        Assert.Equal(8, covered.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => RoleSuitabilityWeights.For((WorldRole)99));
    }

    [Fact]
    public void Suitability_UsesThePublishedWeightRows()
    {
        WorldPlayer player = MakePlayer(
            11,
            (AttributeId.Passing, 82f),
            (AttributeId.Vision, 77f),
            (AttributeId.Finishing, 71f),
            (AttributeId.TopSpeed, 64f),
            (AttributeId.Goalkeeping, 33f));
        player.Personality = PersonalityTraits.Neutral;

        foreach (WorldRole role in Enum.GetValues<WorldRole>())
        {
            float expected = 0f;
            foreach (KeyValuePair<AttributeId, float> entry in RoleSuitabilityWeights.For(role))
            {
                expected += entry.Value * player.Attributes.Current(entry.Key);
            }

            Assert.Equal(expected, IdentityEvaluator.Suitability(player, role), 3);
        }
    }

    [Fact]
    public void Suitability_AddsTheSmallPersonalityTilt()
    {
        // A neutral, uniform player scores exactly their current value in every role.
        WorldPlayer player = MakePlayer(5, (AttributeId.Passing, 60f));
        SetAllCurrent(player, 60f);
        player.Personality = PersonalityTraits.Neutral;

        foreach (WorldRole role in Enum.GetValues<WorldRole>())
        {
            Assert.Equal(60f, IdentityEvaluator.Suitability(player, role), 3);
        }

        // Discipline lifts the defensive roles, creativity lifts the attacking ones, and the central
        // midfielder stays the neutral control at 60.
        player.Personality = new PersonalityTraits(50f, 50f, 80f, 80f);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.Goalkeeper), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.CenterBack), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.FullBack), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.DefensiveMidfielder), 3);
        Assert.Equal(60f, IdentityEvaluator.Suitability(player, WorldRole.CentralMidfielder), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.AttackingMidfielder), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.Winger), 3);
        Assert.Equal(60.9f, IdentityEvaluator.Suitability(player, WorldRole.Striker), 3);
    }

    // =============================================================================================
    // The plan's emergent-identity gate scenarios.
    // =============================================================================================

    [Fact]
    public void PlaymakingProfile_EvaluatesAsCentralOrAttackingMidfielder_WithPlaymakerBridge()
    {
        WorldPlayer player = MakePlayer(
            101,
            (AttributeId.Passing, 90f),
            (AttributeId.Vision, 90f),
            (AttributeId.Positioning, 80f),
            (AttributeId.FirstTouch, 85f),
            (AttributeId.Composure, 80f),
            (AttributeId.Dribbling, 75f),
            (AttributeId.TopSpeed, 40f),
            (AttributeId.Acceleration, 45f),
            (AttributeId.Stamina, 65f),
            (AttributeId.Tackling, 55f),
            (AttributeId.OffTheBall, 60f),
            (AttributeId.Agility, 60f),
            (AttributeId.Goalkeeping, 10f));

        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

        Assert.True(
            identity.PrimaryRole is WorldRole.CentralMidfielder or WorldRole.AttackingMidfielder,
            $"expected CM or AM, got {identity.PrimaryRole}");
        Assert.Contains("Deep-Lying Playmaker", identity.StyleTags);
        Assert.Equal(PlayerArchetype.Playmaker, identity.Archetype);
        Assert.Equal(TacticalRole.CentralMidfielder, identity.TacticalRole);
    }

    [Fact]
    public void FastFinishingProfile_EvaluatesAsStrikerOrWinger_WithInsideForwardBridge()
    {
        WorldPlayer player = MakePlayer(
            202,
            (AttributeId.TopSpeed, 92f),
            (AttributeId.Acceleration, 90f),
            (AttributeId.Finishing, 85f),
            (AttributeId.Dribbling, 85f),
            (AttributeId.OffTheBall, 80f),
            (AttributeId.FirstTouch, 78f),
            (AttributeId.Agility, 78f),
            (AttributeId.ShotPower, 80f),
            (AttributeId.Passing, 60f),
            (AttributeId.Vision, 55f),
            (AttributeId.Positioning, 40f),
            (AttributeId.Stamina, 70f),
            (AttributeId.Tackling, 30f),
            (AttributeId.Strength, 60f),
            (AttributeId.Balance, 65f),
            (AttributeId.Composure, 65f),
            (AttributeId.NaturalReach, 60f),
            (AttributeId.Goalkeeping, 10f));

        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

        Assert.True(
            identity.PrimaryRole is WorldRole.Striker or WorldRole.Winger,
            $"expected ST or W, got {identity.PrimaryRole}");
        Assert.Contains("Speed Demon", identity.StyleTags);
        Assert.Contains("Poacher", identity.StyleTags);
        Assert.Equal(PlayerArchetype.InsideForward, identity.Archetype);
    }

    [Fact]
    public void GoalkeeperProfile_EvaluatesGoalkeeper_AndBridgesToTheGoalkeeperRole()
    {
        WorldPlayer player = MakePlayer(
            303,
            30f,
            (AttributeId.Goalkeeping, 92f),
            (AttributeId.Positioning, 85f),
            (AttributeId.Composure, 80f),
            (AttributeId.Balance, 70f),
            (AttributeId.Agility, 75f),
            (AttributeId.NaturalReach, 85f),
            (AttributeId.Passing, 65f));

        EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

        Assert.Equal(WorldRole.Goalkeeper, identity.PrimaryRole);
        Assert.Equal(TacticalRole.Goalkeeper, identity.TacticalRole);
        Assert.Equal(PlayerArchetype.SweeperKeeper, identity.Archetype);
        Assert.Contains("Sweeper Keeper", identity.StyleTags);
        Assert.Equal(
            IdentityEvaluator.Suitability(player, WorldRole.Goalkeeper),
            identity.PrimaryScore,
            3);
    }

    // =============================================================================================
    // Invariants of a single evaluation.
    // =============================================================================================

    [Fact]
    public void EverySuitability_IsInRange_AndTheListIsSortedDescending()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                seed, 15 + (int)(seed % 10), (BackstoryKind)(seed % 5), seed % 4 == 0);
            EmergentIdentity identity = IdentityEvaluator.Evaluate(player);

            Assert.Equal(8, identity.RoleSuitabilities.Count);
            var seen = new HashSet<WorldRole>();
            for (int i = 0; i < identity.RoleSuitabilities.Count; i++)
            {
                RoleSuitability entry = identity.RoleSuitabilities[i];
                Assert.True(seen.Add(entry.Role), $"seed {seed}: {entry.Role} listed twice.");
                Assert.InRange(entry.Score, 0f, 100f);
                Assert.False(float.IsNaN(entry.Score));
                Assert.Equal(entry.Score, IdentityEvaluator.Suitability(player, entry.Role), 4);

                if (i > 0)
                {
                    Assert.True(
                        identity.RoleSuitabilities[i - 1].Score >= entry.Score,
                        $"seed {seed}: suitabilities are not sorted at index {i}.");
                }
            }

            Assert.Equal(8, seen.Count);
            Assert.Equal(identity.RoleSuitabilities[0].Role, identity.PrimaryRole);
            Assert.Equal(identity.RoleSuitabilities[0].Score, identity.PrimaryScore, 4);
        }
    }

    [Fact]
    public void SecondaryRoles_FollowTheSixPointOrEightyFivePercentRule()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                seed, 16 + (int)(seed % 8), (BackstoryKind)(seed % 5), seed % 5 == 0);
            EmergentIdentity identity = IdentityEvaluator.Evaluate(player);
            float primaryScore = identity.PrimaryScore;

            var expected = new List<WorldRole>();
            foreach (RoleSuitability entry in identity.RoleSuitabilities)
            {
                if (entry.Role == identity.PrimaryRole) continue;
                if (entry.Score >= primaryScore - IdentityEvaluator.SecondaryWithinPoints ||
                    entry.Score >= IdentityEvaluator.SecondaryFraction * primaryScore)
                {
                    expected.Add(entry.Role);
                }
            }

            Assert.Equal(expected.Count, identity.SecondaryRoles.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i], identity.SecondaryRoles[i]);
            }

            Assert.True(identity.SuitedTo(identity.PrimaryRole), $"seed {seed}: primary not suited.");
            foreach (WorldRole secondary in identity.SecondaryRoles)
            {
                Assert.True(identity.SuitedTo(secondary), $"seed {seed}: {secondary} not suited.");
            }
        }
    }

    [Fact]
    public void StyleTags_AreOneOrTwoTagsFromTheKnownSet()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                seed, 15 + (int)(seed % 12), (BackstoryKind)(seed % 5), seed % 3 == 0);

            IReadOnlyList<string> tags = IdentityEvaluator.StyleTagsOf(player);
            Assert.InRange(tags.Count, 1, 2);

            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (string tag in tags)
            {
                Assert.Contains(tag, KnownStyleTags);
                Assert.True(distinct.Add(tag), $"seed {seed}: tag {tag} repeated.");
            }

            IReadOnlyList<string> evaluated = IdentityEvaluator.Evaluate(player).StyleTags;
            Assert.Equal(tags.Count, evaluated.Count);
            for (int i = 0; i < tags.Count; i++) Assert.Equal(tags[i], evaluated[i]);
        }
    }

    [Fact]
    public void Archetype_IsAlwaysADefinedEnumValue()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                seed, 16 + (int)(seed % 10), (BackstoryKind)(seed % 5), seed % 2 == 0);
            EmergentIdentity identity = IdentityEvaluator.Evaluate(player);
            Assert.True(Enum.IsDefined(identity.Archetype), $"seed {seed}: {identity.Archetype} is undefined.");
        }
    }

    // =============================================================================================
    // Style-tag rule order and fallbacks.
    // =============================================================================================

    [Fact]
    public void EachFixedTagRule_MatchesItsThresholds()
    {
        AssertContainsOnlyTag(
            MakePlayer(1, (AttributeId.TopSpeed, 85f), (AttributeId.Acceleration, 85f)),
            "Speed Demon");
        AssertContainsOnlyTag(
            MakePlayer(2, (AttributeId.Passing, 80f), (AttributeId.Vision, 80f), (AttributeId.Positioning, 70f)),
            "Deep-Lying Playmaker");
        AssertContainsOnlyTag(
            MakePlayer(3, (AttributeId.Finishing, 85f), (AttributeId.OffTheBall, 80f)),
            "Poacher");
        AssertContainsOnlyTag(
            MakePlayer(4, (AttributeId.Stamina, 80f), (AttributeId.Tackling, 75f), (AttributeId.Passing, 75f)),
            "Box-to-Box Engine");
        AssertContainsOnlyTag(
            MakePlayer(5, (AttributeId.Strength, 85f), (AttributeId.Balance, 80f), (AttributeId.Finishing, 75f)),
            "Target Forward");
        AssertContainsOnlyTag(
            MakePlayer(6, (AttributeId.Dribbling, 85f), (AttributeId.FirstTouch, 80f), (AttributeId.Agility, 75f)),
            "Technical Dribbler");
        AssertContainsOnlyTag(
            MakePlayer(7, (AttributeId.Tackling, 85f), (AttributeId.Strength, 80f), (AttributeId.Stamina, 80f)),
            "Ball-Winning Destroyer");
        AssertContainsOnlyTag(
            MakePlayer(
                8,
                (AttributeId.Positioning, 80f),
                (AttributeId.Composure, 80f),
                (AttributeId.Passing, 75f),
                (AttributeId.Tackling, 78f)),
            "Composed Ball-Playing CB");
        AssertContainsOnlyTag(
            MakePlayer(
                9,
                (AttributeId.Goalkeeping, 80f),
                (AttributeId.Composure, 75f),
                (AttributeId.Passing, 65f)),
            "Sweeper Keeper");
        AssertContainsOnlyTag(MakePlayer(10, 35f), "Raw Amateur Prospect");
    }

    [Fact]
    public void TagRules_KeepOnlyTheFirstTwoMatches()
    {
        WorldPlayer player = MakePlayer(
            11,
            60f,
            (AttributeId.TopSpeed, 85f),
            (AttributeId.Acceleration, 85f),
            (AttributeId.Passing, 80f),
            (AttributeId.Vision, 80f),
            (AttributeId.Positioning, 70f),
            (AttributeId.Finishing, 80f),
            (AttributeId.OffTheBall, 80f),
            (AttributeId.Dribbling, 80f),
            (AttributeId.FirstTouch, 80f),
            (AttributeId.Agility, 75f),
            (AttributeId.Stamina, 78f),
            (AttributeId.Tackling, 74f));

        IReadOnlyList<string> tags = IdentityEvaluator.StyleTagsOf(player);
        Assert.Equal(2, tags.Count);
        Assert.Equal("Speed Demon", tags[0]);
        Assert.Equal("Deep-Lying Playmaker", tags[1]);
    }

    [Fact]
    public void NoRuleMatch_FallsBackToTheDominantProfile()
    {
        // A flat, unremarkable profile: nothing spikes far enough for a rule, so the balanced tag wins.
        IReadOnlyList<string> balanced = IdentityEvaluator.StyleTagsOf(MakePlayer(12, 55f));
        Assert.Single(balanced);
        Assert.Equal("Balanced Profile", balanced[0]);

        // Strength and reach are the two highest attributes: a frame-led profile.
        IReadOnlyList<string> physical = IdentityEvaluator.StyleTagsOf(
            MakePlayer(13, 40f, (AttributeId.Strength, 80f), (AttributeId.NaturalReach, 90f)));
        Assert.Single(physical);
        Assert.Equal("Physical Presence", physical[0]);
    }

    // =============================================================================================
    // Bridge mappings.
    // =============================================================================================

    [Fact]
    public void ToTacticalRole_MapsTheEightWorldRolesOntoTheMatchEngineRoles()
    {
        Assert.Equal(TacticalRole.Goalkeeper, IdentityEvaluator.ToTacticalRole(WorldRole.Goalkeeper));
        Assert.Equal(TacticalRole.CenterBack, IdentityEvaluator.ToTacticalRole(WorldRole.CenterBack));
        Assert.Equal(TacticalRole.FullBack, IdentityEvaluator.ToTacticalRole(WorldRole.FullBack));
        Assert.Equal(TacticalRole.DefensiveMidfielder, IdentityEvaluator.ToTacticalRole(WorldRole.DefensiveMidfielder));
        Assert.Equal(TacticalRole.CentralMidfielder, IdentityEvaluator.ToTacticalRole(WorldRole.CentralMidfielder));
        Assert.Equal(TacticalRole.CentralMidfielder, IdentityEvaluator.ToTacticalRole(WorldRole.AttackingMidfielder));
        Assert.Equal(TacticalRole.Winger, IdentityEvaluator.ToTacticalRole(WorldRole.Winger));
        Assert.Equal(TacticalRole.Striker, IdentityEvaluator.ToTacticalRole(WorldRole.Striker));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdentityEvaluator.ToTacticalRole((WorldRole)99));
    }

    [Fact]
    public void ToArchetype_MapsTheTagRules()
    {
        Assert.Equal(
            PlayerArchetype.SweeperKeeper,
            IdentityEvaluator.ToArchetype(WorldRole.Goalkeeper, new[] { "Sweeper Keeper" }));
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.Goalkeeper, Array.Empty<string>()));

        Assert.Equal(
            PlayerArchetype.TargetForward,
            IdentityEvaluator.ToArchetype(WorldRole.Striker, new[] { "Target Forward" }));
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.Striker, new[] { "Poacher" }));

        Assert.Equal(
            PlayerArchetype.InsideForward,
            IdentityEvaluator.ToArchetype(WorldRole.Winger, new[] { "Speed Demon" }));
        Assert.Equal(
            PlayerArchetype.InsideForward,
            IdentityEvaluator.ToArchetype(WorldRole.AttackingMidfielder, new[] { "Poacher" }));
        Assert.Equal(
            PlayerArchetype.TouchlineWinger,
            IdentityEvaluator.ToArchetype(WorldRole.Winger, new[] { "Technical Dribbler" }));
        Assert.Equal(
            PlayerArchetype.Playmaker,
            IdentityEvaluator.ToArchetype(WorldRole.AttackingMidfielder, new[] { "Deep-Lying Playmaker" }));

        Assert.Equal(
            PlayerArchetype.Playmaker,
            IdentityEvaluator.ToArchetype(WorldRole.CentralMidfielder, new[] { "Deep-Lying Playmaker" }));
        Assert.Equal(
            PlayerArchetype.BallWinner,
            IdentityEvaluator.ToArchetype(WorldRole.CentralMidfielder, new[] { "Ball-Winning Destroyer" }));

        Assert.Equal(
            PlayerArchetype.BallWinner,
            IdentityEvaluator.ToArchetype(WorldRole.DefensiveMidfielder, new[] { "Ball-Winning Destroyer" }));
        Assert.Equal(
            PlayerArchetype.BallWinner,
            IdentityEvaluator.ToArchetype(WorldRole.DefensiveMidfielder, new[] { "Box-to-Box Engine" }));

        Assert.Equal(
            PlayerArchetype.InvertedFullBack,
            IdentityEvaluator.ToArchetype(WorldRole.FullBack, new[] { "Deep-Lying Playmaker" }));
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.FullBack, Array.Empty<string>()));
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.CenterBack, new[] { "Speed Demon" }));
        Assert.Throws<ArgumentNullException>(
            () => IdentityEvaluator.ToArchetype(WorldRole.Striker, null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IdentityEvaluator.ToArchetype((WorldRole)99, Array.Empty<string>()));
    }

    [Fact]
    public void ToArchetype_PlayerAwareOverload_AddsTheBallPlayingFullBackClause()
    {
        WorldPlayer ballPlaying = MakePlayer(14, (AttributeId.Passing, 80f));
        Assert.Equal(
            PlayerArchetype.InvertedFullBack,
            IdentityEvaluator.ToArchetype(WorldRole.FullBack, Array.Empty<string>(), ballPlaying));

        WorldPlayer limited = MakePlayer(14, (AttributeId.Passing, 60f));
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.FullBack, Array.Empty<string>(), limited));

        // A centre-back with the same passing has no archetype: the clause is fullback-specific.
        Assert.Equal(
            PlayerArchetype.None,
            IdentityEvaluator.ToArchetype(WorldRole.CenterBack, Array.Empty<string>(), ballPlaying));
    }

    // =============================================================================================
    // Tendency bridge.
    // =============================================================================================

    [Fact]
    public void BuildTendencies_AlwaysPassesValidation_AndAllNineValuesAreInRange()
    {
        PlayerArchetype[] archetypes =
        {
            PlayerArchetype.None,
            PlayerArchetype.Playmaker,
            PlayerArchetype.BallWinner,
            PlayerArchetype.InvertedFullBack,
            PlayerArchetype.TouchlineWinger,
            PlayerArchetype.InsideForward,
            PlayerArchetype.TargetForward,
            PlayerArchetype.SweeperKeeper
        };

        for (ulong seed = 1; seed <= 20; seed++)
        {
            WorldPlayer player = PlayerGenerator.GenerateAuthentic(
                seed, 15 + (int)(seed % 15), (BackstoryKind)(seed % 5), seed % 3 == 0);

            foreach (WorldRole role in Enum.GetValues<WorldRole>())
            {
                foreach (PlayerArchetype archetype in archetypes)
                {
                    PlayerTendencies tendencies = IdentityEvaluator.BuildTendencies(player, role, archetype);
                    SimulationConfigValidation.ValidateTendencies(tendencies);
                    AssertTendencyRange(tendencies);
                }
            }

            EmergentIdentity identity = IdentityEvaluator.Evaluate(player);
            AssertTendencyRange(IdentityEvaluator.BuildTendencies(
                player, identity.PrimaryRole, identity.Archetype));
        }
    }

    [Fact]
    public void BuildTendencies_AppliesTheBoundedAttributeDeltas()
    {
        // Extreme currents clamp to the ±0.12 single-axis bound, so a preset can only be leaned.
        WorldPlayer player = MakePlayer(
            21,
            (AttributeId.Finishing, 99f),
            (AttributeId.Positioning, 100f),
            (AttributeId.Passing, 100f),
            (AttributeId.Vision, 100f),
            (AttributeId.Stamina, 1f));

        PlayerTendencies tendencies = IdentityEvaluator.BuildTendencies(
            player, WorldRole.DefensiveMidfielder, PlayerArchetype.None);

        // Defensive-midfielder preset: shooting 0.40, positioning 0.80, direct pass 0.45, risk 0.42,
        // pressing 0.70; cross/scan/sweep fall back to the neutral baselines.
        Assert.Equal(0.52f, tendencies.ShootingAppetite01, 4);
        Assert.Equal(0.92f, tendencies.PositionalDiscipline01, 4);
        Assert.Equal(0.57f, tendencies.DirectPassPreference01, 4);
        Assert.Equal(0.54f, tendencies.RiskTolerance01, 4);
        Assert.Equal(0.58f, tendencies.PressingAggression01, 4);
        Assert.Equal(0.61f, tendencies.CrossPreference01, 4);
        Assert.Equal(0.62f, tendencies.ScanAppetite01, 4);
        Assert.Equal(0.50f, tendencies.SweepAggression01, 4);
    }

    [Fact]
    public void BuildTendencies_OnlyKeepersGetTheSweepDelta()
    {
        WorldPlayer keeper = MakePlayer(22, 40f, (AttributeId.Goalkeeping, 90f));

        PlayerTendencies asKeeper = IdentityEvaluator.BuildTendencies(
            keeper, WorldRole.Goalkeeper, PlayerArchetype.None);
        Assert.Equal(0.58f, asKeeper.SweepAggression01, 4); // (90 - 50) / 500 = 0.08 over the neutral 0.50

        PlayerTendencies asOutfielder = IdentityEvaluator.BuildTendencies(
            keeper, WorldRole.Striker, PlayerArchetype.None);
        Assert.Equal(0.50f, asOutfielder.SweepAggression01, 4);
    }

    // =============================================================================================
    // Determinism and validation.
    // =============================================================================================

    [Fact]
    public void Evaluate_IsDeterministic_AndEqualContentComparesEqual()
    {
        WorldPlayer player = MakePlayer(
            31,
            (AttributeId.Passing, 80f),
            (AttributeId.Vision, 78f),
            (AttributeId.Tackling, 72f),
            (AttributeId.TopSpeed, 60f),
            (AttributeId.Finishing, 65f));

        EmergentIdentity first = IdentityEvaluator.Evaluate(player);
        EmergentIdentity second = IdentityEvaluator.Evaluate(player);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(first.PrimaryRole, second.PrimaryRole);
        Assert.Equal(first.PrimaryScore, second.PrimaryScore, 4);

        // Two players generated from the same seed and arguments are indistinguishable players.
        WorldPlayer twinA = PlayerGenerator.GenerateAuthentic(77UL, 18, BackstoryKind.EliteAcademy);
        WorldPlayer twinB = PlayerGenerator.GenerateAuthentic(77UL, 18, BackstoryKind.EliteAcademy);
        Assert.Equal(IdentityEvaluator.Evaluate(twinA), IdentityEvaluator.Evaluate(twinB));
    }

    [Fact]
    public void Evaluate_RejectsNullPlayers()
    {
        Assert.Throws<ArgumentNullException>(() => IdentityEvaluator.Evaluate(null!));
        Assert.Throws<ArgumentNullException>(() => IdentityEvaluator.StyleTagsOf(null!));
        Assert.Throws<ArgumentNullException>(() => IdentityEvaluator.BuildTendencies(
            null!, WorldRole.Striker, PlayerArchetype.None));
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>
    /// Generates a deterministic player, raises every ceiling to the maximum and then writes the supplied
    /// current values (all other attributes sit at <paramref name="baseValue"/>).
    /// </summary>
    private static WorldPlayer MakePlayer(
        ulong seed, float baseValue, params (AttributeId Id, float Current)[] overrides)
    {
        WorldPlayer player = PlayerGenerator.GenerateAuthentic(seed, 21, BackstoryKind.RegionalAcademy);
        foreach (AttributeId id in AttributeMetadata.All)
        {
            player.Attributes.SetCeiling(id, 99f);
            player.Attributes.SetCurrent(id, baseValue);
        }

        foreach ((AttributeId id, float current) in overrides) player.Attributes.SetCurrent(id, current);
        return player;
    }

    private static WorldPlayer MakePlayer(ulong seed, params (AttributeId Id, float Current)[] overrides) =>
        MakePlayer(seed, 50f, overrides);

    private static void SetAllCurrent(WorldPlayer player, float value)
    {
        foreach (AttributeId id in AttributeMetadata.All) player.Attributes.SetCurrent(id, value);
    }

    private static void AssertContainsOnlyTag(WorldPlayer player, string expectedTag)
    {
        IReadOnlyList<string> tags = IdentityEvaluator.StyleTagsOf(player);
        Assert.Single(tags);
        Assert.Equal(expectedTag, tags[0]);
    }

    private static void AssertTendencyRange(PlayerTendencies tendencies)
    {
        Assert.InRange(tendencies.RiskTolerance01, 0f, 1f);
        Assert.InRange(tendencies.DirectPassPreference01, 0f, 1f);
        Assert.InRange(tendencies.ShootingAppetite01, 0f, 1f);
        Assert.InRange(tendencies.DribblePreference01, 0f, 1f);
        Assert.InRange(tendencies.PositionalDiscipline01, 0f, 1f);
        Assert.InRange(tendencies.PressingAggression01, 0f, 1f);
        Assert.InRange(tendencies.CrossPreference01, 0f, 1f);
        Assert.InRange(tendencies.ScanAppetite01, 0f, 1f);
        Assert.InRange(tendencies.SweepAggression01, 0f, 1f);
    }
}
