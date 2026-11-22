using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for the 2026-11-22 scoring audit (the scoring half):
///
/// <list type="number">
/// <item>AUD-01 — <c>AttackingWidth01</c> sat in the <b>denominator</b> of the width term in
///   <see cref="ReceptionValueModel.Evaluate"/> and <see cref="JobRegionScoring.Score"/>, so at the same
///   lateral coordinate a wide side scored half of a narrow side's width credit (0.40 vs 0.80 at
///   |z| = 15 m), every style was pushed to the touchline, and the widest profile's target (37.4 m) was
///   off a 34 m half-pitch. The audit's required properties are asserted here directly: monotone in
///   <c>AttackingWidth01</c> at a fixed <c>|z|</c>, target-width semantics instead of raw <c>|z|</c>,
///   a bounded/continuous/pitch-safe result, and one convention shared by both call sites.</item>
/// <item>AUD-13a — <see cref="TeamTacticalCoordinator.SeedPossession(TeamId?, float, float)"/> always
///   rebuilt the contexts with <c>homeAttackDirX = 1</c> and <c>ballX = 0</c> and left both zones at
///   MiddleThird, so a scenario fragment starting in the second period, or with the ball inside the 3 m
///   zone deadband, was misclassified on tick 1. The audit tests seed with the real direction and ball
///   X and compare against the live turnover classifier.</item>
/// </list>
/// Each test states in a comment which assertion fails on the pre-fix code — the constants chosen are
/// picked from the old formula (<c>|z| / (HalfWidth * (0.55 + 0.55 * width01))</c> for reception,
/// <c>|z| / ((HalfWidth - 3) * (0.55 + 0.55 * width01))</c> for job regions) so the failure is
/// arithmetic, not incidental. The two exceptions are marked where they occur: the old-signature
/// compatibility guard is designed to pass both ways, and the shared-helper test cannot compile before
/// the helper exists (that non-existence is what AUD-01(d) fixes).
/// </summary>
public sealed class AudFixScoringTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    /// <summary><see cref="JobRegionScoring"/>'s private width weight; AUD-01 did not change it.</summary>
    private const float JobWidthWeight = 0.25f;

    private static readonly PitchDimensions Pitch = new();

    // ---------------------------------------------------------------------------------------------
    // AUD-01 — one width convention, never inverted by the profile
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-01(a): at any fixed lateral coordinate a higher <c>AttackingWidth01</c> must score the width
    /// term monotonically greater-or-equal. Pre-fix this fails at the very first non-central sample:
    /// |z| = 2 m gave a narrow side 2/18.7 = 0.107 and the widest side 2/37.4 = 0.053 — the wider side
    /// was penalised for standing in the same place.
    /// </summary>
    [Fact]
    public void AUD01_AWiderProfile_NeverScoresLowerAtTheSameLateralCoordinate()
    {
        float[] widths = { 0f, 0.25f, 0.5f, 0.65f, 0.82f, 1f };

        for (float z = 0f; z <= Pitch.HalfWidth; z += 2f)
        {
            float narrower = ReceptionStructure(widths[0], z);
            for (int i = 1; i < widths.Length; i++)
            {
                float wider = ReceptionStructure(widths[i], z);
                Assert.True(wider >= narrower - 1e-4f,
                    $"at |z| = {z:F0} m width {widths[i]:F2} scored {wider:F3}, below " +
                    $"width {widths[i - 1]:F2}'s {narrower:F3} at the same coordinate (AUD-01 inversion)");
                narrower = wider;
            }
        }
    }

    /// <summary>
    /// AUD-01(b): a narrow side must not score the touchline as its best width. Pre-fix both samples are
    /// exactly 1.0 — the old 18.7 m denominator saturated by |z| = 18.7 and stayed saturated at the
    /// touchline — so the strict comparison fails on the pre-fix code.
    /// </summary>
    [Fact]
    public void AUD01_ANarrowSide_DoesNotTreatTheTouchlineAsItsBestWidth()
    {
        float touchline = ReceptionStructure(0f, Pitch.HalfWidth - 0.5f);
        // 19 m is inside the narrow profile's target band (0.55..0.70 of the half-width).
        float atTarget = ReceptionStructure(0f, 19f);

        Assert.True(touchline < atTarget - 0.2f,
            $"the narrow side scored the touchline {touchline:F2} against {atTarget:F2} at its own " +
            "target width; the touchline must not be its maximum");
    }

    /// <summary>
    /// AUD-01(b): a wide side must prefer a wider position than a narrow side. Pre-fix the narrow side
    /// is at full credit from 18.7 m all the way to the touchline, so "the widest full-credit position of
    /// the wide side is further out" is false (the wide side never even reaches 1.0: 34/37.4 = 0.91).
    /// </summary>
    [Fact]
    public void AUD01_AWiderSide_PrefersAWiderPositionThanANarrowSide()
    {
        float narrowReach = WidestFullCreditPosition(0f);
        float wideReach = WidestFullCreditPosition(1f);

        Assert.True(wideReach > narrowReach,
            $"the wide side's width is satisfied out to {wideReach:F1} m and the narrow side's only to " +
            $"{narrowReach:F1} m; a wider profile has to hold its width further from the centre line");

        float wideAtWide = ReceptionStructure(1f, Pitch.HalfWidth - 1f);
        float narrowAtWide = ReceptionStructure(0f, Pitch.HalfWidth - 1f);
        Assert.True(wideAtWide > narrowAtWide + 0.4f,
            $"at |z| = {Pitch.HalfWidth - 1f:F0} m the wide side scored {wideAtWide:F2} and the narrow " +
            $"side {narrowAtWide:F2}; the wide side must prefer the wider position");
    }

    /// <summary>
    /// AUD-01(c): the credit is a finite 0..1 number, continuous, and its target is reachable on the
    /// pitch. Pre-fix the first assertion fails: the widest profile's denominator was 34 * 1.10 = 37.4 m,
    /// so the touchline only reached 34/37.4 = 0.909 and full credit was impossible on the pitch.
    /// </summary>
    [Fact]
    public void AUD01_WidthCredit_IsBoundedContinuousAndReachesItsTargetOnThePitch()
    {
        Assert.Equal(1f, ReceptionStructure(1f, Pitch.HalfWidth), 3);
        Assert.Equal(1f, ReceptionStructure(0.65f, Pitch.HalfWidth - 8f), 3);

        float[] widths = { 0f, 0.3f, 0.65f, 1f };
        var previous = new float[widths.Length];
        for (float z = 0f; z <= 2f * Pitch.HalfWidth; z += 0.25f)
        {
            for (int i = 0; i < widths.Length; i++)
            {
                float structure = ReceptionStructure(widths[i], z);
                Assert.True(float.IsFinite(structure), $"non-finite width credit at |z| = {z:F2} m");
                Assert.InRange(structure, 0f, 1f);
                // 0.25 m steps: the steepest leg of the convention is < 0.02 per step, so a jump larger
                // than that is a discontinuity, not the ramp or the over-width penalty.
                Assert.True(MathF.Abs(structure - previous[i]) <= 0.02f,
                    $"width credit jumped {MathF.Abs(structure - previous[i]):F3} at |z| = {z:F2} m " +
                    $"(width {widths[i]:F2})");
                previous[i] = structure;
            }
        }

        // Pitch-safe: past the touchline the position is clamped, it does not keep accruing credit.
        Assert.Equal(ReceptionStructure(0.65f, Pitch.HalfWidth), ReceptionStructure(0.65f, 1.5f * Pitch.HalfWidth), 5);
    }

    /// <summary>
    /// AUD-01(d): reception valuation and job-region scoring use the same width term, so they cannot
    /// drift apart again. Pre-fix this fails because the reception divided by
    /// <c>HalfWidth * widthScale</c> while the job scorer divided by <c>(HalfWidth - 3) * widthScale</c>:
    /// a 0 → 1 profile-width change at |z| = 22 m moved them by 0.412 and 0.355 respectively (not the
    /// same term scaled by the weight).
    /// </summary>
    [Fact]
    public void AUD01_ReceptionValuationAndJobRegionScoring_UseTheSameWidthTerm()
    {
        float[] widths = { 0f, 0.3f, 0.65f, 1f };
        float[] laterals = { 4f, 10f, 16f, 22f, 28f, 33f };

        foreach (float z in laterals)
        {
            for (int i = 1; i < widths.Length; i++)
            {
                float receptionDelta = ReceptionStructure(widths[i], z) - ReceptionStructure(widths[i - 1], z);
                float jobDelta = JobRegionWidthScore(widths[i], z) - JobRegionWidthScore(widths[i - 1], z);

                Assert.Equal(JobWidthWeight * receptionDelta, jobDelta, 4);
            }
        }
    }

    /// <summary>
    /// AUD-01: the shared convention's target is a fraction of the half-width, always on the pitch, and
    /// a position at that target scores full credit for every profile. This test references the helper
    /// the fix introduced, so it cannot compile against the pre-fix code at all — that non-existence is
    /// exactly the drift AUD-01(d) names; the cross-site test above is the pre-fix-runnable witness.
    /// </summary>
    [Fact]
    public void AUD01_TheSharedWidthConvention_HasAReachableMonotoneTarget()
    {
        Assert.True(AttackingWidthPreference.TargetFraction01(1f) > AttackingWidthPreference.TargetFraction01(0f));

        for (float width = 0f; width <= 1f; width += 0.05f)
        {
            float target = AttackingWidthPreference.TargetLateralMeters(width, Pitch);
            Assert.True(target > 0f && target <= Pitch.HalfWidth,
                $"width {width:F2} asked for a target {target:F2} m off centre, outside the half-width");
            Assert.Equal(1f, AttackingWidthPreference.Score(width, target, Pitch), 3);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-13a — SeedPossession seeds the real direction and ball position
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// AUD-13a: seeding must read both zones from the real ball X through the real attack direction.
    /// Pre-fix both contexts are MiddleThird for every row (the old call hardcoded 1f/0f), so the
    /// FinalThird/OwnThird rows fail. The -19 m row is deliberately inside the 3 m deadband
    /// (17.5..20.5 m of progress): a seeded passage is a new passage, so the hard third boundary decides
    /// it, exactly as the live turnover path does (see the next test).
    /// </summary>
    [Theory]
    [InlineData(-1f, -19f, TeamZone.FinalThird, TeamZone.OwnThird)]
    [InlineData(-1f, -25f, TeamZone.FinalThird, TeamZone.OwnThird)]
    [InlineData(-1f, 10f, TeamZone.MiddleThird, TeamZone.MiddleThird)]
    [InlineData(1f, -19f, TeamZone.OwnThird, TeamZone.FinalThird)]
    [InlineData(1f, 25f, TeamZone.FinalThird, TeamZone.OwnThird)]
    [InlineData(1f, 0f, TeamZone.MiddleThird, TeamZone.MiddleThird)]
    public void AUD13a_SeedingUsesTheRealAttackDirectionAndBallPosition(
        float homeAttackDirX, float ballX, TeamZone expectedHome, TeamZone expectedAway)
    {
        var coordinator = new TeamTacticalCoordinator(Pitch);

        coordinator.SeedPossession(TeamId.Home, homeAttackDirX, ballX);

        Assert.Equal(expectedHome, coordinator.HomeContext.Zone);
        Assert.Equal(expectedAway, coordinator.AwayContext.Zone);
        Assert.Equal(TeamPhase.Attacking, coordinator.HomeContext.Phase);
        Assert.Equal(TeamPhase.Defending, coordinator.AwayContext.Phase);
    }

    /// <summary>
    /// AUD-13a: "the same classifier as the normal update path". A live turnover at the same ball
    /// position re-reads the zones with the turnover branch; seeding the same passage must produce the
    /// same zones. Pre-fix the seeded coordinator answers MiddleThird/MiddleThird where the live path
    /// answers FinalThird/OwnThird, so this fails.
    /// </summary>
    [Fact]
    public void AUD13a_SeededZones_MatchTheLiveTurnoverRead()
    {
        const float homeAttackDirX = -1f;
        var liveBall = new BallState { Position = new Vector3(-19f, 0f, 0f), LastTouchedByTeam = TeamId.Away };
        var live = new TeamTacticalCoordinator(Pitch);
        live.ObservePossession(Controlled(TeamId.Home), liveBall, homeAttackDirX, Dt);
        // Away wins the ball at x = -19 m: the turnover branch re-reads both zones from the ball.
        live.ObservePossession(Controlled(TeamId.Away), liveBall, homeAttackDirX, Dt);

        var seeded = new TeamTacticalCoordinator(Pitch);
        seeded.SeedPossession(TeamId.Away, homeAttackDirX, liveBall.Position.X);

        Assert.Equal(live.HomeContext.Zone, seeded.HomeContext.Zone);
        Assert.Equal(live.AwayContext.Zone, seeded.AwayContext.Zone);
    }

    /// <summary>
    /// AUD-13a: the old one-argument call must keep working (callers outside this fix's ownership still
    /// use it) and must keep the old centre-spot classification until they pass the real values. This
    /// passes pre- and post-fix; it is the compatibility guard on the optional parameters.
    /// </summary>
    [Fact]
    public void AUD13a_DefaultSeedArguments_KeepTheOldCentreSpotBehaviour()
    {
        var coordinator = new TeamTacticalCoordinator(Pitch);

        coordinator.SeedPossession(TeamId.Home);

        Assert.Equal(TeamZone.MiddleThird, coordinator.HomeContext.Zone);
        Assert.Equal(TeamZone.MiddleThird, coordinator.AwayContext.Zone);
        Assert.Equal(TeamId.Home, coordinator.ConfirmedPossessingTeam);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    /// <summary>The reception model's structure term for a profile width at a lateral coordinate.</summary>
    private static float ReceptionStructure(float attackingWidth01, float lateralAbsMeters)
    {
        var model = new ReceptionValueModel(Pitch);
        FootballerObservation observation = Observation(attackingWidth01);
        return model.Evaluate(
            observation, receiverId: 8, new Vector2(0f, lateralAbsMeters), receptionPressure01: 0.2f).Structure01;
    }

    /// <summary>
    /// A width duty's <see cref="JobRegionScoring.Score"/> with no teammates and no believed opponents:
    /// every term except the width reward is then independent of the profile width, so differencing two
    /// widths isolates the width term.
    /// </summary>
    private static float JobRegionWidthScore(float attackingWidth01, float lateralAbsMeters)
    {
        var region = new JobRegion(
            new Vector3(10f, 0f, lateralAbsMeters), JobRegionScoring.RadiusOf(TacticalJob.FarSideWidth));
        return JobRegionScoring.Score(
            region,
            TacticalJob.FarSideWidth,
            referenceXZ: new Vector2(0f, 0f),
            Array.Empty<AssignedRegion>(),
            candidatePlayerId: 1,
            new TeamBelief(TeamId.Home),
            attackDirX: 1f,
            Pitch,
            attackingWidth01);
    }

    /// <summary>The furthest lateral position from the centre line that still earns full width credit.</summary>
    private static float WidestFullCreditPosition(float attackingWidth01)
    {
        float widest = 0f;
        for (float z = 0f; z <= Pitch.HalfWidth; z += 0.5f)
        {
            if (ReceptionStructure(attackingWidth01, z) >= 1f - 1e-3f) widest = z;
        }
        return widest;
    }

    private static FootballerObservation Observation(float attackingWidth01)
    {
        return new FootballerObservation
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = Vector3.Zero,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = Vector3.Zero,
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = Vector3.Zero,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 700,
            VisiblePlayers = new List<PerceivedEntity>(),
            TeamProfile = new TeamTacticalProfile { AttackingWidth01 = attackingWidth01 },
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static BallPossessionSnapshot Controlled(TeamId team) =>
        new(BallControlState.Controlled, team, ControllingPlayerId: 8, IntendedReceiverId: null);
}
