using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.1 — the six style presets, and the one axis the style vocabulary was missing.
///
/// A style is a set of stated preferences on the one decision pipeline, so these tests pin what a style has to
/// be to be worth shipping: every preset is a valid profile, the pre-A9.1 presets are untouched by the new axis
/// (bit for bit, so their trajectories keep their meaning), the explicit man-orientation axis moves A6.3's
/// zone/man mix and the marker's goal-side standoff at constant pressing and compactness, and the team
/// directness now reaches the carrier's forward-pass preference instead of only the counter-attack transition
/// gate. None of these is a new model: every assertion is about an existing scorer reading a stated number.
/// </summary>
public sealed class A9StylePresetTests
{
    private static readonly float Dt = SimConstants.FixedDeltaTime;
    private static readonly PitchDimensions Pitch = new();

    /// <summary>The six configurations A9.1's vocabulary names, one preset each.</summary>
    private static readonly TeamTacticalProfile[] ShippedStyles =
    {
        TeamTacticalProfile.PossessionControl,
        TeamTacticalProfile.Direct,
        TeamTacticalProfile.DirectCounterattack,
        TeamTacticalProfile.HighPressGegenpress,
        TeamTacticalProfile.LowBlock,
        TeamTacticalProfile.ManOriented
    };

    private static readonly TeamTacticalProfile[] LegacyPresets =
    {
        TeamTacticalProfile.PossessionControl,
        TeamTacticalProfile.DirectCounterattack,
        TeamTacticalProfile.HighPressGegenpress
    };

    // ---------------------------------------------------------------------------------------------
    // The shipped vocabulary
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Six named configurations, each a valid profile that differs from the neutral control and from every
    /// other preset. A preset that validated but said nothing would be a menu entry, not a style.
    /// </summary>
    [Fact]
    public void TheSixShippedStyles_AreValidAndDistinctNamedConfigurations()
    {
        Assert.Equal(6, ShippedStyles.Length);

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < ShippedStyles.Length; i++)
        {
            TeamTacticalProfile style = ShippedStyles[i];
            SimulationConfigValidation.ValidateTacticalProfile(style, style.Name);
            Assert.True(names.Add(style.Name), $"two presets share the name '{style.Name}'");
            Assert.NotEqual(new TeamTacticalProfile().Name, style.Name);

            for (int j = i + 1; j < ShippedStyles.Length; j++)
                Assert.False(SameNumbers(style, ShippedStyles[j]),
                    $"'{style.Name}' and '{ShippedStyles[j].Name}' are the same profile with different names");
        }

        // The neutral control is not a style: it is what a style is compared against, and a fixture that
        // names no profile must keep getting it.
        var neutral = new TeamTacticalProfile();
        foreach (TeamTacticalProfile style in ShippedStyles)
            Assert.False(SameNumbers(neutral, style), $"'{style.Name}' is the neutral profile");
    }

    /// <summary>
    /// The presets state what their names say — the declared orderings are the vocabulary, so a preset whose
    /// numbers contradict its name would be a misleading menu entry (the A9.1 report reads these axes).
    /// </summary>
    [Fact]
    public void TheStylePresetsStateTheStyleTheyAreNamedFor()
    {
        // Possession: widest shape, least direct, highest positional freedom.
        Assert.True(TeamTacticalProfile.PossessionControl.AttackingWidth01 >
                    TeamTacticalProfile.Direct.AttackingWidth01);
        Assert.True(TeamTacticalProfile.PossessionControl.AttackingWidth01 >
                    TeamTacticalProfile.LowBlock.AttackingWidth01);
        Assert.True(TeamTacticalProfile.PossessionControl.AttackingWidth01 >
                    TeamTacticalProfile.ManOriented.AttackingWidth01);
        Assert.True(TeamTacticalProfile.PossessionControl.Directness01 <
                    TeamTacticalProfile.Direct.Directness01);
        Assert.True(TeamTacticalProfile.PossessionControl.Directness01 <
                    TeamTacticalProfile.LowBlock.Directness01);
        Assert.True(TeamTacticalProfile.PossessionControl.PositionalFreedom01 >
                    TeamTacticalProfile.LowBlock.PositionalFreedom01);

        // Direct is the most direct; counterattacking is direct too but from a deep block.
        Assert.True(TeamTacticalProfile.Direct.Directness01 > TeamTacticalProfile.DirectCounterattack.Directness01);
        Assert.True(TeamTacticalProfile.Direct.DefensiveLineHeight01 >
                    TeamTacticalProfile.DirectCounterattack.DefensiveLineHeight01);
        Assert.True(TeamTacticalProfile.Direct.Compactness01 <
                    TeamTacticalProfile.DirectCounterattack.Compactness01);

        // Low block is the deepest, most compact and least pressing defensive configuration of all six.
        // (The presets are get-only properties that build a fresh profile per access, so the loop skips the
        // low block by name rather than by reference.)
        TeamTacticalProfile lowBlock = TeamTacticalProfile.LowBlock;
        foreach (TeamTacticalProfile other in ShippedStyles)
        {
            if (other.Name == lowBlock.Name) continue;
            Assert.True(lowBlock.DefensiveLineHeight01 < other.DefensiveLineHeight01,
                $"low block is not deeper than '{other.Name}'");
            Assert.True(lowBlock.PressingIntensity01 < other.PressingIntensity01,
                $"low block presses harder than '{other.Name}'");
        }

        Assert.True(TeamTacticalProfile.LowBlock.Compactness01 > TeamTacticalProfile.DirectCounterattack.Compactness01);
        Assert.True(TeamTacticalProfile.LowBlock.Compactness01 > TeamTacticalProfile.HighPressGegenpress.Compactness01);
        Assert.True(TeamTacticalProfile.LowBlock.CounterPressDurationSeconds <
                    TeamTacticalProfile.DirectCounterattack.CounterPressDurationSeconds);

        // High press presses harder than the man-oriented side, but the two mark differently: the press side
        // earns its mix from pressing, the man-oriented side from the explicit axis and stands closer.
        Assert.True(TeamTacticalProfile.HighPressGegenpress.PressingIntensity01 >
                    TeamTacticalProfile.ManOriented.PressingIntensity01);
        Assert.True(TeamTacticalProfile.ManOriented.ManOrientation01 >
                    TeamTacticalProfile.HighPressGegenpress.ManOrientation01);
        Assert.True(DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.ManOriented) <
                    DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.HighPressGegenpress) - 0.2f);
        Assert.Equal(3, DefensiveLineModel.MarkerCount(TeamTacticalProfile.ManOriented));
    }

    // ---------------------------------------------------------------------------------------------
    // Neutrality: the pre-A9.1 profiles keep every number and every derived value
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The three pre-A9.1 presets and the neutral default do not name the new axis, and the axis is a bias
    /// around its neutral value: the derived zone/man share is the pre-A9.1 two-term formula <b>exactly</b>
    /// (the bias is a product with zero, so no float rounding can move it) and the marker standoff is still
    /// 1.9 m. That is what lets the legacy presets keep their trajectories' character while the new styles are
    /// added, and it is why a fixture that mentions no style needs no change.
    /// </summary>
    [Fact]
    public void TheLegacyPresets_AreUntouchedByTheNewMarkingAxis()
    {
        var profiles = new List<TeamTacticalProfile>
        {
            new TeamTacticalProfile(),
            TeamTacticalProfile.PossessionControl,
            TeamTacticalProfile.DirectCounterattack,
            TeamTacticalProfile.HighPressGegenpress
        };

        foreach (TeamTacticalProfile profile in profiles)
        {
            Assert.Equal(0.50f, profile.ManOrientation01);
            Assert.Equal(PreA91ManShare(profile), DefensiveLineModel.ManShare01(profile));
            Assert.Equal(1.9f, DefensiveLineModel.MarkerStandoffMeters(profile));
        }

        // The marker counts the existing A6.3 test pins are unchanged, and the neutral control is still the
        // zonal one marker it was: the pre-A9.1 numbers, not re-tuned ones.
        Assert.Equal(1, DefensiveLineModel.MarkerCount(TeamTacticalProfile.DirectCounterattack));
        Assert.Equal(2, DefensiveLineModel.MarkerCount(TeamTacticalProfile.PossessionControl));
        Assert.Equal(3, DefensiveLineModel.MarkerCount(TeamTacticalProfile.HighPressGegenpress));
        Assert.Equal(1, DefensiveLineModel.MarkerCount(new TeamTacticalProfile()));
    }

    // ---------------------------------------------------------------------------------------------
    // The explicit man-orientation axis
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The axis is a real third term of the zone/man mix: at the same pressing intensity and compactness it
    /// crosses one marker threshold (two marks two, the same profile with a high man orientation marks three),
    /// it is monotone in the field, and it stays subordinate — it cannot on its own turn a passive side into a
    /// three-man-marking side. This is the property the derived mix could not express before A9.1.
    /// </summary>
    [Fact]
    public void ManOrientation_IsTheThirdTermOfTheZoneManMix()
    {
        var zonal = new TeamTacticalProfile { PressingIntensity01 = 0.70f, Compactness01 = 0.75f };
        var marking = new TeamTacticalProfile
        {
            PressingIntensity01 = 0.70f,
            Compactness01 = 0.75f,
            ManOrientation01 = 0.95f
        };

        Assert.Equal(2, DefensiveLineModel.MarkerCount(zonal));
        Assert.Equal(3, DefensiveLineModel.MarkerCount(marking));
        Assert.True(DefensiveLineModel.ManShare01(marking) > DefensiveLineModel.ManShare01(zonal));
        Assert.InRange(DefensiveLineModel.ManShare01(marking), 0f, 1f);

        // Monotone, in range, every step.
        float previous = -1f;
        for (int step = 0; step <= 10; step++)
        {
            var profile = new TeamTacticalProfile
            {
                PressingIntensity01 = 0.80f,
                Compactness01 = 0.70f,
                ManOrientation01 = step / 10f
            };

            float share = DefensiveLineModel.ManShare01(profile);
            Assert.InRange(share, 0f, 1f);
            Assert.True(share > previous, $"man share did not rise at step {step}");
            previous = share;
        }

        // Subordinate: the axis alone cannot make a deep, passive side a three-man-marking side.
        Assert.Equal(1, DefensiveLineModel.MarkerCount(new TeamTacticalProfile
        {
            PressingIntensity01 = 0.10f,
            Compactness01 = 0.40f,
            ManOrientation01 = 1.0f
        }));
    }

    /// <summary>
    /// The standoff a marker holds from his man is the profile's: a man-oriented side stands closer than a
    /// zonal side of the same pressing, the neutral preset is still the single 1.9 m constant, and the field is
    /// monotone across its range. The value reaches the allocator, not just this helper — see
    /// <see cref="TheMarkerStandoffReachesTheJobAllocator"/>.
    /// </summary>
    [Fact]
    public void AManOrientedSideStandsCloserToItsMan()
    {
        Assert.Equal(1.9f, DefensiveLineModel.MarkerStandoffMeters(new TeamTacticalProfile()));
        Assert.True(DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.ManOriented) <
                    DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.PossessionControl));
        Assert.True(DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.PossessionControl) <
                    DefensiveLineModel.MarkerStandoffMeters(TeamTacticalProfile.LowBlock));

        float previous = float.MaxValue;
        for (int step = 0; step <= 10; step++)
        {
            float standoff = DefensiveLineModel.MarkerStandoffMeters(new TeamTacticalProfile
            {
                ManOrientation01 = step / 10f
            });

            Assert.InRange(standoff, 1.4f, 2.4f);
            Assert.True(standoff < previous, $"standoff did not tighten at step {step}");
            previous = standoff;
        }
    }

    /// <summary>
    /// The axis is not only a helper: two runs of the job allocator over the same picture, differing only in
    /// the profile's <see cref="TeamTacticalProfile.ManOrientation01"/>, pick the same markers and place them
    /// at different goal-side standoffs. This is the seam A9.1 changed in <c>TeamJobs</c>, so a regression that
    /// left the constant in place would pass the pure-model tests and fail this one.
    /// </summary>
    [Fact]
    public void TheMarkerStandoffReachesTheJobAllocator()
    {
        var zonal = new TeamTacticalProfile
        {
            PressingIntensity01 = 0.70f,
            Compactness01 = 0.75f,
            ManOrientation01 = 0.50f
        };
        var marking = new TeamTacticalProfile
        {
            PressingIntensity01 = 0.70f,
            Compactness01 = 0.75f,
            ManOrientation01 = 0.95f
        };

        (int count, float standoff) zonalRun = MarkerRun(zonal);
        (int count, float standoff) markingRun = MarkerRun(marking);

        Assert.Equal(2, zonalRun.count);
        Assert.Equal(2, markingRun.count);
        Assert.Equal(DefensiveLineModel.MarkerStandoffMeters(zonal), zonalRun.standoff, precision: 2);
        Assert.Equal(DefensiveLineModel.MarkerStandoffMeters(marking), markingRun.standoff, precision: 2);
        Assert.True(markingRun.standoff < zonalRun.standoff - 0.3f,
            $"the allocator ignored the profile standoff: {markingRun.standoff:F2} m vs {zonalRun.standoff:F2} m");
    }

    // ---------------------------------------------------------------------------------------------
    // Team directness now reaches the carrier
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A4.4's pass preference used to read only a player's own direct-pass tendency, so a team's
    /// <see cref="TeamTacticalProfile.Directness01"/> reached only the counter-attack transition gate and the
    /// restart menu: a "direct" side did not value a forward ball more than a possession side did. A9.1 adds the
    /// profile term symmetric around the neutral 0.50 — the two half-steps (0.20→0.50 and 0.50→0.80) move the
    /// same pass by the same amount, which is the property that keeps the neutral profile neutral.
    /// </summary>
    [Fact]
    public void TeamDirectness_MovesTheForwardPassUtility()
    {
        float patient = ForwardPassUtility(0.20f);
        float neutral = ForwardPassUtility(0.50f);
        float direct = ForwardPassUtility(0.80f);

        Assert.True(direct > neutral, $"a direct side did not value the forward ball more: {direct:F4} vs {neutral:F4}");
        Assert.True(neutral > patient, $"a patient side did not value it less: {neutral:F4} vs {patient:F4}");
        Assert.Equal(neutral - patient, direct - neutral, precision: 3);

        // The neutral value is the pre-A9.1 value: the default profile names no directness beyond the neutral
        // 0.50, so the term contributes exactly zero to every pass it scores.
        Assert.Equal(0.50f, new TeamTacticalProfile().Directness01);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one look at the job allocator over a fixed picture (five home centre-backs against two away
    /// strikers) and reports how many home markers the picture produced and how far the markers stand from the
    /// men they picked up. Only the home profile differs between calls.
    /// </summary>
    private static (int count, float standoff) MarkerRun(TeamTacticalProfile homeProfile)
    {
        var homeCbA = Player(102, TacticalRole.CenterBack, new Vector3(-25f, 0f, -8f));
        var homeCbB = Player(103, TacticalRole.CenterBack, new Vector3(-25f, 0f, 8f));
        var homeCbC = Player(104, TacticalRole.CenterBack, new Vector3(-25f, 0f, 0f));
        var homeCbD = Player(105, TacticalRole.CenterBack, new Vector3(-25f, 0f, 5f));
        var awayA = Player(201, TacticalRole.Striker, new Vector3(-20f, 0f, -10f), TeamId.Away);
        var awayB = Player(202, TacticalRole.Striker, new Vector3(-20f, 0f, 0f), TeamId.Away);
        var players = new List<FootballerState> { homeCbA, homeCbB, homeCbC, homeCbD, awayA, awayB };

        var homeFormation = new TeamFormation(TeamId.Home, Pitch);
        homeFormation.AssignFromPositions(new[] { homeCbA, homeCbB, homeCbC, homeCbD }, 1f);
        var awayFormation = new TeamFormation(TeamId.Away, Pitch);
        awayFormation.AssignFromPositions(new[] { awayA, awayB }, -1f);

        var coordinator = new TeamJobCoordinator(Pitch);
        var ball = new BallState
        {
            Position = awayA.Position with { Y = 0.11f },
            PreviousPosition = awayA.Position with { Y = 0.11f },
            Velocity = Vector3.Zero
        };
        var possession = new BallPossessionSnapshot(BallControlState.Controlled, TeamId.Away, awayA.Id, null);

        coordinator.Update(
            players, ball, possession, possessingTeam: TeamId.Away,
            homeResponderId: null, awayResponderId: null, homeDirX: 1f, tick: 1,
            homeProfile: homeProfile,
            awayProfile: TeamTacticalProfile.DirectCounterattack,
            homeOpponents: TeamBelief.FromTruth(TeamId.Home, players),
            awayOpponents: TeamBelief.FromTruth(TeamId.Away, players),
            homeFormation: homeFormation, awayFormation: awayFormation);

        int count = 0;
        float standoffSum = 0f;
        var positionById = new Dictionary<int, Vector3>();
        foreach (FootballerState player in players) positionById[player.Id] = player.Position;

        foreach (FootballerState player in new[] { homeCbA, homeCbB, homeCbC, homeCbD })
        {
            TeamJobAssignment? duty = coordinator.Get(player.Id);
            if (duty is null || duty.Value.Job != TacticalJob.Marker) continue;
            if (duty.Value.TargetPlayerId is not int targetId) continue;

            count++;
            Vector3 anchor = duty.Value.Anchor;
            Vector3 threat = positionById[targetId];
            standoffSum += Vector2.Distance(new Vector2(anchor.X, anchor.Z), new Vector2(threat.X, threat.Z));
        }

        return (count, count > 0 ? standoffSum / count : -1f);
    }

    /// <summary>
    /// The utility of the one forward pass in a fixed picture, as a function of the team's directness only.
    /// The picture has a single unblocked receiver fifteen metres up the pitch and no opponent in his lane, so
    /// the pass's completion, reception value and loss cost are identical across calls; only A4.4's preference
    /// half can move.
    /// </summary>
    private static float ForwardPassUtility(float directness01)
    {
        var passer = Player(9, TacticalRole.CentralMidfielder, Vector3.Zero);
        var mate = Player(8, TacticalRole.Striker, new Vector3(15f, 0f, 3f));
        var ball = new BallState
        {
            Position = new Vector3(0.3f, 0.11f, 0f),
            LastTouchedByTeam = TeamId.Home,
            LastTouchedByPlayerId = 9
        };

        var profile = new TeamTacticalProfile { Directness01 = directness01 };
        var observation = new PerceptionSystem().BuildObservation(
            passer,
            new[] { passer, mate },
            ball,
            ball.LastTouchedByTeam,
            MatchPhase.FirstHalf,
            10f,
            1f,
            false,
            profile,
            new SeededRandom(42));

        var controller = new TacticalUtilityAiController(Pitch);
        for (int tick = 0; tick < 30; tick++)
            controller.Decide(observation, Dt);

        foreach (UtilityCandidate candidate in controller.LastCandidates)
        {
            if (candidate.ActionName.StartsWith("Pass(", StringComparison.Ordinal)) return candidate.UtilityScore;
        }

        throw new InvalidOperationException("the fixture produced no pass candidate");
    }

    private static FootballerState Player(
        int id,
        TacticalRole role,
        Vector3 position,
        TeamId team = TeamId.Home) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        TacticalAnchor = position,
        FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f)
    };

    /// <summary>The pre-A9.1 two-term zone/man mix, mirrored here so neutrality is checked against the old formula.</summary>
    private static float PreA91ManShare(in TeamTacticalProfile profile) =>
        Math.Clamp(
            0.35f +
            0.90f * (Math.Clamp(profile.PressingIntensity01, 0f, 1f) - 0.5f) +
            0.50f * (Math.Clamp(profile.Compactness01, 0f, 1f) - 0.5f),
            0f,
            1f);

    private static bool SameNumbers(TeamTacticalProfile left, TeamTacticalProfile right) =>
        left.Directness01 == right.Directness01 &&
        left.Tempo01 == right.Tempo01 &&
        left.AttackingWidth01 == right.AttackingWidth01 &&
        left.DefensiveLineHeight01 == right.DefensiveLineHeight01 &&
        left.Compactness01 == right.Compactness01 &&
        left.PressingIntensity01 == right.PressingIntensity01 &&
        left.RiskTolerance01 == right.RiskTolerance01 &&
        left.CounterPressDurationSeconds == right.CounterPressDurationSeconds &&
        left.PositionalFreedom01 == right.PositionalFreedom01 &&
        left.ShortGoalkeeperDistribution == right.ShortGoalkeeperDistribution &&
        left.ManOrientation01 == right.ManOrientation01;
}
