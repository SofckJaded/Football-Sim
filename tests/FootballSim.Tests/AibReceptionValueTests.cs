using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AIB-4b / AIB-6b — the reception-value half of the 2026-11-22 behaviour audit.
///
/// <list type="number">
/// <item><b>AIB-4b — central receptions were structurally penalised.</b>
///   <see cref="AttackingWidthPreference.Score"/> turns <c>(|z| / HalfWidth) / 0.55</c> into
///   <c>Structure01 = 0</c> at the centre line and <c>1</c> on the wings (≥ 18.7 m) for <i>every</i>
///   profile, so a completed flank pass collected a structure credit a completed central or half-space
///   pass could never collect. The fix keeps that shared width convention for build-up (AUD-01's
///   meaning is unchanged) and exchanges it in the final third for a central-threat credit: central
///   corridor plus distance to the opponent's last line.</item>
/// <item><b>AIB-6b — <c>Progression01</c> had no penalty for losing ground.</b> The old expression
///   <c>Clamp01((arrivalProjX − BallProjX) / ProgressionSpanMeters)</c> scored a 25 m backward
///   reception exactly like a lateral one (both 0), so a recycle kept the same gain as a sideways
///   ball and still collected the full pressure/turnability credit. The value term is now signed:
///   lost ground pays up to <c>−ProgressionWeight</c> per span. The exposed
///   <see cref="ReceptionValueTerms.Progression01"/> keeps its old meaning and 0..1 bounds; the loss
///   lives inside <see cref="ReceptionValueTerms.Value"/>.</item>
/// </list>
///
/// Pure-function tests: each fixture builds the reception directly and states, in the comment above
/// the assertion, the old formula's arithmetic that the assertion falsifies.
/// </summary>
public sealed class AibReceptionValueTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly ReceptionValueModel Model = new(Pitch);

    /// <summary><see cref="TeamTacticalProfile.PossessionControl"/>'s width, the profile most fixtures use.</summary>
    private const float DefaultWidth01 = 0.82f;

    // =============================================================================================
    // AIB-4b — the final-third structure credit is central threat, not raw width
    // =============================================================================================

    /// <summary>
    /// The audit's own comparison: at the same arrival projection, the same ball, the same pressure and
    /// the same defensive geometry, a central final-third reception must not be outscored by the wide one.
    ///
    /// The fixture puts both receptions outside shooting range (28.5 m and 41.6 m from goal), so the
    /// shooting term is zero for both, and mirrors the goal-side marker so turnability, next options and
    /// ball safety are identical. Everything except the structure term is therefore equal, and the old
    /// formula decides it on <c>|z| / (HalfWidth × 0.55)</c>: central <c>Structure01 = 0.00</c>, wide at
    /// |z| = 32 m <c>= 1.00</c> (the widest profile, no over-width penalty yet) or <c>≈ 0.71</c> (the
    /// narrowest), so the old central value is <b>0.08 × wideCredit lower</b> and both assertions fail.
    /// The fix replaces the credit in the final third, so the central credit is ≈0.63 and the wide one
    /// ≈0.38 for the widest profile and ≈0.32 for the others.
    /// </summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.82f)]
    [InlineData(1f)]
    public void AIB4b_CentralFinalThirdReception_BeatsAnEquallyPressuredWideOne(float attackingWidth01)
    {
        // Ball on the halfway line, 4 m short of the final-third blend end; the last line is 34 m up the
        // pitch, mirrored either side of centre.
        FootballerObservation obs = Observation(
            attackingWidth01,
            new Vector2(20f, 0f),
            (14, new Vector2(34f, 0f), Vector2.Zero),
            (15, new Vector2(34f, 32f), Vector2.Zero));

        Vector2 centralPoint = new(24f, 0f);
        Vector2 widePoint = new(24f, 32f);
        Vector2 goal = new(Pitch.HalfLength, 0f);

        // This fixture isolates the structure term only if neither reception is in shooting range.
        Assert.True(Vector2.Distance(centralPoint, goal) > ActionValueScale.ShotFarMeters,
            "the central fixture point is in shooting range, so it would not isolate the structure term");
        Assert.True(Vector2.Distance(widePoint, goal) > ActionValueScale.ShotFarMeters,
            "the wide fixture point is in shooting range, so it would not isolate the structure term");

        ReceptionValueTerms central = Model.Evaluate(obs, 9, centralPoint, 0.3f);
        ReceptionValueTerms wide = Model.Evaluate(obs, 9, widePoint, 0.3f);

        // The rest of the fixture really is symmetric (same 10 m goal-side room, same distance from goal
        // band, same ball): only the positional-structure term may differ.
        Assert.Equal(central.ShootingChance01, wide.ShootingChance01);
        Assert.Equal(central.Turnability01, wide.Turnability01, 5);
        Assert.Equal(central.NextOptions01, wide.NextOptions01, 5);
        Assert.Equal(central.Progression01, wide.Progression01, 5);
        Assert.Equal(central.BallSafety01, wide.BallSafety01, 5);

        Assert.True(central.Structure01 > wide.Structure01 + 0.2f,
            $"width {attackingWidth01:F2}: central structure {central.Structure01:F3} vs wide " +
            $"{wide.Structure01:F3} — pre-fix it was 0.000 against the wing's |z| credit, the AIB-4b penalty");
        Assert.True(central.Value > wide.Value,
            $"width {attackingWidth01:F2}: a central final-third reception ({central.Value:F3}) must not be " +
            $"worth less than an equally-pressured wide one ({wide.Value:F3}); pre-fix the wide structure " +
            $"credit alone made it so");
    }

    /// <summary>
    /// AUD-01 must not be reintroduced: below the width → central-threat blend the structure term is exactly
    /// the shared width convention, for every profile, lateral coordinate and projection. This is the
    /// compatibility guard on the shared API: the old expression and the new one are the same credit here.
    ///
    /// AUD-P6 / Fix 5.3 re-baselined the samples: the blend now starts at
    /// <see cref="ReceptionValueModel.FinalThirdBlendStartProjMeters"/> = <b>+5 m</b> (the old window started
    /// at the final-third line, <c>HalfLength / 3 = +17.5 m</c>), so the build-up range this fixture can
    /// assert exactly is <c>x &lt; +5 m</c>: <c>FinalThirdBlend01(x) = (x − 5) / (18 − 5) ≤ 0 ⇒ 0</c>, and
    /// the credit reduces to <c>AttackingWidthPreference.Score</c>. The two old samples at +12 m and +17 m
    /// were inside the old build-up range but are now 7/13 and 12/13 through the blend, so they no longer
    /// belong in an "unchanged" fixture; the blend at those depths is pinned by
    /// <c>Aib2RunAndSpaceValueTests.AIB2_SpaceValue_MidfieldReception_BlendsOnlySlightly</c>.
    /// </summary>
    [Fact]
    public void AIB4b_WidthPreference_IsUnchangedForBuildUpReceptions()
    {
        float[] widths = { 0f, 0.5f, DefaultWidth01, 1f };
        float[] laterals = { 0f, 6f, 12f, 18f, 24f, 33f };
        // Every sample is behind the AUD-P6 blend start (+5 m), where the credit is pure width again.
        float[] projections = { -45f, -20f, 0f, 4f };

        foreach (float projection in projections)
        {
            foreach (float lateral in laterals)
            {
                float narrowerCredit = -1f;
                foreach (float width in widths)
                {
                    ReceptionValueTerms terms = Model.Evaluate(
                        Observation(width, Vector2.Zero), 9, new Vector2(projection, lateral), 0.2f);
                    float expected = AttackingWidthPreference.Score(width, lateral, Pitch);

                    Assert.Equal(expected, terms.Structure01, 5);

                    // And AUD-01's monotonicity in the profile width still holds at the same coordinate.
                    Assert.True(terms.Structure01 >= narrowerCredit - 1e-5f,
                        $"at x = {projection:F0} m, |z| = {lateral:F0} m: width {width:F2} scored " +
                        $"{terms.Structure01:F3}, below the narrower profile's {narrowerCredit:F3}");
                    narrowerCredit = terms.Structure01;
                }
            }
        }
    }

    /// <summary>
    /// The blend from width credit to central threat must not invert AUD-01: at a fixed position the
    /// credit is still monotonically non-decreasing in <c>AttackingWidth01</c>, because the central term
    /// does not read the profile at all and the blend keeps weight <c>(1 − f)</c> on the width credit.
    /// This passes on the pre-fix code too; it is the guard that the fix did not create a new inversion.
    /// </summary>
    [Fact]
    public void AIB4b_TheFinalThirdBlend_StaysMonotoneInTheProfileWidth()
    {
        float[] widths = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        float[] projections = { 17f, 18f, 20f, 22f, 24f, 26f, 40f };
        float[] laterals = { 0f, 10f, 20f, 30f };

        foreach (float projection in projections)
        {
            foreach (float lateral in laterals)
            {
                float narrowerCredit = -1f;
                foreach (float width in widths)
                {
                    ReceptionValueTerms terms = Model.Evaluate(
                        Observation(width, Vector2.Zero), 9, new Vector2(projection, lateral), 0.2f);
                    Assert.True(terms.Structure01 >= narrowerCredit - 1e-5f,
                        $"at x = {projection:F0} m, |z| = {lateral:F0} m: width {width:F2} scored " +
                        $"{terms.Structure01:F3}, below the narrower profile's {narrowerCredit:F3}");
                    narrowerCredit = terms.Structure01;
                }
            }
        }
    }

    /// <summary>
    /// The fix must not simply move the bias to a different place: once the blend has completed, the
    /// final-third structure credit is the same for every profile — the central corridor is worth the
    /// same to a narrow side and a wide one. Pre-fix the central credit is 0.00 for every profile
    /// (<c>reachedWidth = (0 / HalfWidth) / 0.55</c>), so the "≥ 0.5" and the equality both fail.
    /// </summary>
    [Fact]
    public void AIB4b_TheCentralCredit_IsProfileIndependentAndNotHalved()
    {
        float first = float.NaN;
        foreach (float width in new[] { 0f, 0.3f, DefaultWidth01, 1f })
        {
            ReceptionValueTerms terms = Model.Evaluate(
                Observation(width, new Vector2(25f, 0f)), 9, new Vector2(30f, 0f), 0.2f);

            if (float.IsNaN(first)) first = terms.Structure01;
            Assert.Equal(first, terms.Structure01, 4);
            Assert.True(terms.Structure01 >= 0.5f,
                $"width {width:F2}: a central final-third reception scored {terms.Structure01:F3}; pre-fix " +
                "it was 0.00 at the centre line for every profile, the AIB-4b penalty");
        }
    }

    /// <summary>
    /// The "between the lines" half of the central-threat term, against the repo's last-line convention:
    /// the line is the <b>deepest outfield defender</b> (the largest attack-relative projection, keeper
    /// excluded), so a reception <b>behind</b> that line earns full line credit and one <b>in front</b> of
    /// it earns less as the gap grows. Fixture: last line at x = 34, with an away midfielder at 30 and an
    /// away forward pressing high at x = 5 — the forward's small projection must NOT become the line, and
    /// the keeper at 50 must not either.
    ///
    /// Credit arithmetic (|z| = 10 m, so centrality = 1): behind = 0.60 + 0.40 × 1.000 = 1.000; four
    /// metres in front = 0.60 + 0.40 × (1 − 4/18) = 0.911, a delta of 0.4 × 0.222 = 0.089. The first
    /// version of this test used the smallest projection (the opponents' most advanced man) as the line,
    /// which pinned both credits at 1.000 — the review blocker — and expected a +0.1 delta it could never
    /// get; the threshold below is the 0.089 the correct convention produces.
    /// </summary>
    [Fact]
    public void AIB4b_AReceptionBehindTheLastLine_EarnsMoreStructureCreditThanOneInFrontOfIt()
    {
        var visible = new[]
        {
            new PerceivedEntity(16, TeamId.Away, TacticalRole.Striker,
                new Vector3(5f, 0f, 0f), Vector3.Zero, 1f),
            new PerceivedEntity(14, TeamId.Away, TacticalRole.CenterBack,
                new Vector3(34f, 0f, 10f), Vector3.Zero, 1f),
            new PerceivedEntity(15, TeamId.Away, TacticalRole.CentralMidfielder,
                new Vector3(30f, 0f, 20f), Vector3.Zero, 1f),
            new PerceivedEntity(25, TeamId.Away, TacticalRole.Goalkeeper,
                new Vector3(50f, 0f, 0f), Vector3.Zero, 1f)
        };
        FootballerObservation obs = ObservationWithPlayers(DefaultWidth01, new Vector2(18f, 10f), visible);

        // Two metres goal-side of the last line, and four metres in front of it, both central (|z| = 10).
        ReceptionValueTerms behind = Model.Evaluate(obs, 9, new Vector2(36f, 10f), 0.3f);
        ReceptionValueTerms inFront = Model.Evaluate(obs, 9, new Vector2(30f, 10f), 0.3f);

        // Full line credit behind the line: this fails if the keeper is taken as the line (0.889) or if
        // the opponents' advanced forward is (the min-projection bug gave the same 1.000 to both).
        Assert.Equal(1f, behind.Structure01, 3);
        Assert.True(behind.Structure01 > inFront.Structure01 + 0.05f,
            $"behind the line {behind.Structure01:F3} vs in front of it {inFront.Structure01:F3}; the " +
            "expected delta is 0.4 × (1 − (1 − 4/18)) = 0.089");
    }

    /// <summary>
    /// A picture containing only the opponent goalkeeper has no outfield last line, so the line half of
    /// the credit must fall back to the documented neutral <see cref="ReceptionValueModel.UnknownBackLineProximity01"/>
    /// — not full credit and, under the pre-fix min-projection loop, not the 0 that the keeper's own
    /// projection produced. At the centre line the corridor credit is 1, so the structure credit is
    /// 0.60 + 0.40 × 0.50 = 0.80 (the buggy code returned 0.60).
    /// </summary>
    [Fact]
    public void AIB4b_KeeperOnlyPicture_ReturnsTheNeutralLineProximity()
    {
        var visible = new[]
        {
            new PerceivedEntity(25, TeamId.Away, TacticalRole.Goalkeeper,
                new Vector3(50f, 0f, 0f), Vector3.Zero, 1f)
        };
        FootballerObservation obs = ObservationWithPlayers(DefaultWidth01, Vector2.Zero, visible);

        ReceptionValueTerms terms = Model.Evaluate(obs, 9, new Vector2(30f, 0f), 0.2f);

        float expected = ReceptionValueModel.CentralCorridorWeight
                         + ReceptionValueModel.BetweenLinesWeight
                         * ReceptionValueModel.UnknownBackLineProximity01;
        Assert.Equal(expected, terms.Structure01, 4);
    }

    /// <summary>
    /// The width → central-threat exchange is blended, so the structure credit is continuous across the
    /// final-third boundary instead of stepping at it. With no visible opponents the term is monotone in
    /// both directions of the blend (0 → 0.8 centrally, 1 → 0.25 on the wing), and every 0.25 m step is
    /// far below the 0.04 bound. (The old code's width credit is continuous too — this guards the fix.)
    /// </summary>
    [Fact]
    public void AIB4b_StructureCredit_IsContinuousAcrossTheFinalThirdBoundary()
    {
        FootballerObservation obs = Observation(DefaultWidth01, Vector2.Zero);
        float previous = -1f;

        foreach (float lateral in new[] { 0f, 12f, 30f })
        {
            previous = -1f;
            for (float x = 5f; x <= 45f; x += 0.25f)
            {
                float structure = Model.Evaluate(obs, 9, new Vector2(x, lateral), 0.2f).Structure01;
                if (previous >= 0f)
                {
                    Assert.True(MathF.Abs(structure - previous) <= 0.04f,
                        $"structure jumped {MathF.Abs(structure - previous):F3} between x = {x - 0.25f:F2} " +
                        $"and {x:F2} m at |z| = {lateral:F0} m");
                }

                previous = structure;
            }
        }
    }

    // =============================================================================================
    // AIB-6b — losing ground lowers the reception value
    // =============================================================================================

    /// <summary>
    /// The audit's case: a ball received 15 m behind the ball carrier is worth less than the same ball
    /// received laterally, at the same pressure. Everything else in the fixture is identical (no
    /// opponents, so the turn room and next options match; both positions sit in the middle third, so the
    /// width credit matches), so the old formula's <c>Clamp01(−15 / 30) = 0</c> and
    /// <c>Clamp01(0 / 30) = 0</c> made the two values <b>exactly equal</b> and the assertion fails on the
    /// pre-fix code. New: the lateral reception pays 0 and the backward one <c>−0.25 × 15/30 = −0.125</c>.
    /// </summary>
    [Fact]
    public void AIB6b_BackwardArrival_ScoresBelowLateralArrival()
    {
        FootballerObservation obs = Observation(DefaultWidth01, new Vector2(10f, 8f));

        ReceptionValueTerms lateral = Model.Evaluate(obs, 9, new Vector2(10f, 8f), 0.4f);
        ReceptionValueTerms backward = Model.Evaluate(obs, 9, new Vector2(-5f, 8f), 0.4f);

        // The exposed field keeps its documented meaning: both are the forward 0..1 component, i.e. 0.
        Assert.Equal(0f, lateral.Progression01, 5);
        Assert.Equal(lateral.Progression01, backward.Progression01, 5);

        Assert.True(backward.Value < lateral.Value - 0.05f,
            $"a reception 15 m behind the ball ({backward.Value:F3}) must be worth less than a lateral one " +
            $"({lateral.Value:F3}); pre-fix both were equal because the raw advance was clamped at zero");
    }

    /// <summary>
    /// The loss scales with the ground lost: 30 m behind the ball is worth less than 15 m behind it.
    /// Pre-fix both clamp to 0 progression and score identically, so this fails on the old formula.
    /// </summary>
    [Fact]
    public void AIB6b_DeeperBackwardArrival_IsWorthLess()
    {
        FootballerObservation obs = Observation(DefaultWidth01, new Vector2(10f, 8f));

        ReceptionValueTerms shortBackward = Model.Evaluate(obs, 9, new Vector2(-5f, 8f), 0.4f);
        ReceptionValueTerms deepBackward = Model.Evaluate(obs, 9, new Vector2(-20f, 8f), 0.4f);

        Assert.True(deepBackward.Value < shortBackward.Value - 0.05f,
            $"30 m back ({deepBackward.Value:F3}) must be worth less than 15 m back " +
            $"({shortBackward.Value:F3}); pre-fix both scored the same clamped 0 progression");
    }

    /// <summary>
    /// The territory loss is bounded at one span (30 m), so it cannot dominate the scale: two receptions
    /// further back than a full span score the same as each other (both own-goal distances here are at or
    /// beyond the full safety distance, so only the progression term differs from a shallower ball).
    /// </summary>
    [Fact]
    public void AIB6b_TheTerritoryLoss_IsBoundedAtOneSpan()
    {
        FootballerObservation obs = Observation(DefaultWidth01, new Vector2(10f, 8f));

        ReceptionValueTerms oneSpanBack = Model.Evaluate(obs, 9, new Vector2(-20f, 8f), 0.4f);
        ReceptionValueTerms twoSpansBack = Model.Evaluate(obs, 9, new Vector2(-22f, 8f), 0.4f);

        Assert.Equal(oneSpanBack.Value, twoSpansBack.Value, 4);
        Assert.Equal(0f, oneSpanBack.Progression01, 5);
        Assert.Equal(0f, twoSpansBack.Progression01, 5);
    }

    /// <summary>
    /// The exposed <c>Progression01</c> keeps its documented meaning (forward advance / span, clamped to
    /// 0..1) along the whole pitch, and the value rises as the reception moves up it — losing less ground
    /// never lowers the value. Fixture has no opponents, so tumability and next options are constant; the
    /// central corridor makes the final-third structure monotone too.
    /// </summary>
    [Fact]
    public void AIB6b_ProgressionIsTheForwardComponent_AndTheValueRisesUpThePitch()
    {
        FootballerObservation obs = Observation(DefaultWidth01, Vector2.Zero);
        float previousValue = float.NegativeInfinity;
        float previousProgression = -1f;

        for (float x = -48f; x <= 40f; x += 4f)
        {
            ReceptionValueTerms terms = Model.Evaluate(obs, 9, new Vector2(x, 5f), 0.2f);

            Assert.Equal(Math.Clamp(x / ReceptionValueModel.ProgressionSpanMeters, 0f, 1f),
                terms.Progression01, 4);
            Assert.True(terms.Progression01 >= previousProgression - 1e-5f);
            Assert.True(terms.Value >= previousValue - 1e-4f,
                $"value fell from {previousValue:F4} to {terms.Value:F4} at x = {x:F0} m");

            previousValue = terms.Value;
            previousProgression = terms.Progression01;
        }
    }

    // =============================================================================================
    // Bounds
    // =============================================================================================

    /// <summary>
    /// Every exposed term stays finite and in 0..1 and the composed value stays finite and inside
    /// <c>[0, MaxValue]</c> across the whole pitch, every profile width and every pressure — the insurance
    /// that neither the signed progression nor the final-third replacement can run away.
    /// </summary>
    [Fact]
    public void AIB4b_AIB6b_ValueIsFiniteAndBoundedAcrossThePitchAndEveryProfile()
    {
        var visible = new (int Id, Vector2 Position, Vector2 Velocity)[]
        {
            (14, new Vector2(40f, 8f), Vector2.Zero),
            (15, new Vector2(20f, -15f), Vector2.Zero),
            (10, new Vector2(10f, -10f), Vector2.Zero)
        };
        float[] widths = { 0f, 0.3f, 0.65f, DefaultWidth01, 1f };
        float[] pressures = { 0f, 0.5f, 1f };

        foreach (float width in widths)
        {
            FootballerObservation obs = Observation(width, Vector2.Zero, visible);
            for (float x = -Pitch.HalfLength; x <= Pitch.HalfLength; x += 5f)
            {
                for (float z = -Pitch.HalfWidth; z <= Pitch.HalfWidth; z += 8.5f)
                {
                    foreach (float pressure in pressures)
                    {
                        ReceptionValueTerms terms = Model.Evaluate(obs, 9, new Vector2(x, z), pressure);
                        AssertBounded(terms);
                        Assert.True(terms.Value <= Model.MaxValue,
                            $"x = {x:F1}, z = {z:F1}, width {width:F2}, pressure {pressure:F2}: value " +
                            $"{terms.Value:F3} above the documented bound {Model.MaxValue:F3}");
                    }
                }
            }
        }
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private static void AssertBounded(ReceptionValueTerms terms)
    {
        Assert.True(float.IsFinite(terms.Value), "the reception value is not finite");
        Assert.InRange(terms.Value, 0f, Model.MaxValue);
        Assert.InRange(terms.Progression01, 0f, 1f);
        Assert.InRange(terms.Turnability01, 0f, 1f);
        Assert.InRange(terms.NextOptions01, 0f, 1f);
        Assert.InRange(terms.ShootingChance01, 0f, 1f);
        Assert.InRange(terms.Pressure01, 0f, 1f);
        Assert.InRange(terms.Structure01, 0f, 1f);
        Assert.InRange(terms.BallSafety01, 0f, 1f);
    }

    private static FootballerObservation Observation(
        Vector2 ballXZ,
        params (int Id, Vector2 Position, Vector2 Velocity)[] players) =>
        Observation(DefaultWidth01, ballXZ, players);

    /// <summary>
    /// The observation the model reads: the ball and the believed players. Ids above 13 are the away
    /// side (the convention the existing model tests use), so a fixture can name an opponent inline.
    /// </summary>
    private static FootballerObservation Observation(
        float attackingWidth01,
        Vector2 ballXZ,
        params (int Id, Vector2 Position, Vector2 Velocity)[] players)
    {
        var visible = new List<PerceivedEntity>(players.Length);
        foreach ((int id, Vector2 position, Vector2 velocity) in players)
        {
            TeamId team = id >= 14 ? TeamId.Away : TeamId.Home;
            visible.Add(new PerceivedEntity(
                id, team, TacticalRole.CentralMidfielder,
                new Vector3(position.X, 0f, position.Y),
                new Vector3(velocity.X, 0f, velocity.Y),
                1f));
        }

        return ObservationWithPlayers(attackingWidth01, ballXZ, visible);
    }

    private static FootballerObservation ObservationWithPlayers(
        float attackingWidth01,
        Vector2 ballXZ,
        IReadOnlyList<PerceivedEntity> visible)
    {
        return new FootballerObservation
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(ballXZ.X, 0f, ballXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 20f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 700,
            VisiblePlayers = visible,
            TeamProfile = new TeamTacticalProfile { AttackingWidth01 = attackingWidth01 },
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
            SelfAttributes = new PlayerAttributes()
        };
    }
}
