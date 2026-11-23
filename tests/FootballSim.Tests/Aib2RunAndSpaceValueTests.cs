using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// AUD-P1 / AUD-P6 — the two model-level valuations the 2026-11-23 passing/space audit found defective:
///
/// <list type="number">
/// <item><b>AUD-P1 (critical) — an uncontested run was priced neutral.</b>
///   <see cref="RunScoring.ArrivalAdvantage"/> returned <see cref="RunScoring.NeutralArrivalAdvantage"/>
///   (0.5) whenever its scan found no candidate opponent, and a candidate is only admitted when the
///   straight-line plausibility filter (<c>horizon × 11 m/s</c>) passes. A wide-open run — the exact
///   picture where opponents are visible but nobody can reach the meeting point — therefore took the same
///   0.5 as "no opponent is known at all". Through <c>RunCandidateGenerator.ConfidenceFrom</c>
///   (<c>0.20 + 0.55 × arrival + 0.25 × passAvailability</c>) that cost the run 0.275 confidence, which
///   <c>PassSuccessModel</c> then charged as its receiver term: an unmarked run into space lost ~30 % of
///   its completion probability to a pass to feet. The fix keeps 0.5 only for genuine ignorance and returns
///   1.0 when opponents <i>are</i> visible and none can get there.</item>
/// <item><b>AUD-P6 / Fix 5.3 (high) — the width → central-threat blend started too late.</b>
///   <see cref="ReceptionValueModel"/> blended the build-up width credit into the central-threat credit only
///   from <c>x = HalfLength / 3 = +17.5 m</c> to <c>+25.5 m</c>, so every midfield reception was still
///   priced on raw width — the centre line scored 0 and the wings 1 for <i>every</i> profile, which kept
///   pulling the attack to the flank through build-up. The window is now +5 m → +18 m, named
///   <see cref="ReceptionValueModel.FinalThirdBlendStartProjMeters"/> /
///   <see cref="ReceptionValueModel.FinalThirdBlendEndProjMeters"/>, with the same single
///   <see cref="ReceptionValueModel.StructureWeight"/> as the only weight.</item>
/// </list>
///
/// Model-level tests in the house style: every fixture is a synthetic
/// <see cref="FootballerObservation"/> and its comment states the pre-fix arithmetic that the assertion
/// falsifies (the guard fixtures explicitly say where the pre-fix value is the same, because that half of
/// the behaviour is what must not regress).
/// </summary>
public sealed class Aib2RunAndSpaceValueTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly ReceptionValueModel Model = new(Pitch);

    /// <summary>The carrier sits on <see cref="CarrierXZ"/> and the run meets at <see cref="MeetXZ"/>.</summary>
    private static readonly Vector2 CarrierXZ = Vector2.Zero;
    private static readonly Vector2 MeetXZ = new(20f, 0f);

    /// <summary><see cref="TeamTacticalProfile.PossessionControl"/>'s width, the profile the fixtures use.</summary>
    private const float DefaultWidth01 = 0.82f;

    // =============================================================================================
    // AUD-P1 — only *contested* and *unknown* are different pictures; "nobody can get there" is won
    // =============================================================================================

    /// <summary>
    /// AUD-P1's "no evidence" half: with no opponent in the picture the arrival race is genuinely unknown,
    /// so the answer must stay the neutral <see cref="RunScoring.NeutralArrivalAdvantage"/> (0.5).
    ///
    /// This passes on the pre-fix code too (the old scan returned 0.5 here): it is the guard that the fix
    /// did not turn ignorance into confidence. A team-mate standing on the meeting point is not an opponent
    /// either — own-team entities are skipped before the evidence flag.
    /// </summary>
    [Fact]
    public void AIB2_RunValue_NoOpponentVisible_ReturnsNeutral()
    {
        FootballerObservation empty = Observation(Array.Empty<PerceivedEntity>());
        Assert.Equal(RunScoring.NeutralArrivalAdvantage,
            RunScoring.ArrivalAdvantage(empty, MeetXZ, mySeconds: 1f));

        FootballerObservation mateOnly = Observation(new[] { Entity(9, TeamId.Home, MeetXZ) });
        Assert.Equal(RunScoring.NeutralArrivalAdvantage,
            RunScoring.ArrivalAdvantage(mateOnly, MeetXZ, mySeconds: 1f));
    }

    /// <summary>
    /// AUD-P1's reported picture: opponents <b>are</b> visible and none can reach the meeting region inside
    /// the horizon, so the runner wins by a mile and the answer is
    /// <see cref="RunScoring.UncontestedArrivalAdvantage"/> (1.0).
    ///
    /// Arithmetic: <c>mySeconds = 1.0</c> ⇒ <c>horizon = min(1.0 + 1.0, 7) = 2.0 s</c>; the plausibility
    /// filter admits an opponent only when his straight distance to <c>meetXZ</c> is
    /// <c>≤ 2.0 × 11 = 22 m</c>. Both opponents below are 40 m and 45 m away, so they are skipped by that
    /// second, different filter. Pre-fix the skipped opponent left <c>candidateIndex &lt; 0</c> and the method
    /// returned <b>0.5</b>; post-fix the scan saw an opponent and returns <b>1.0</b>. Through
    /// <c>ConfidenceFrom</c> that alone is the difference between
    /// <c>0.20 + 0.55 × 0.5 = 0.475</c> and <c>0.20 + 0.55 × 1.0 = 0.750</c> before the pass term.
    /// </summary>
    [Fact]
    public void AIB2_RunValue_OpponentsVisibleButUnreachable_ReturnsUncontested()
    {
        FootballerObservation one = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(40f, 0f))
        });
        Assert.Equal(RunScoring.UncontestedArrivalAdvantage,
            RunScoring.ArrivalAdvantage(one, MeetXZ, mySeconds: 1f));

        // A second, even more distant opponent changes nothing: the picture is still "nobody can get there".
        FootballerObservation two = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(40f, 0f)),
            Entity(26, TeamId.Away, MeetXZ + new Vector2(0f, -45f))
        });
        Assert.Equal(1f, RunScoring.ArrivalAdvantage(two, MeetXZ, mySeconds: 1f));
    }

    /// <summary>
    /// The documented AUD-P1 rule for a corrupt estimate: an opponent whose estimated position is not finite
    /// is <b>no evidence</b>. He cannot contest the run (there is no place on the pitch to contest it from)
    /// and he does not turn "nothing known" into "a won race".
    ///
    /// Pre-fix this returns 0.5 as well — the non-finite skip already existed — so this fixture pins the rule
    /// the two-branch fix had to choose, not the defect itself.
    /// </summary>
    [Fact]
    public void AIB2_RunValue_NonFiniteOpponentEstimate_IsNoEvidence()
    {
        PerceivedEntity corrupt = new(
            25, TeamId.Away, TacticalRole.CenterBack,
            new Vector3(float.NaN, 0f, 0f), Vector3.Zero, 0.95f);

        Assert.Equal(RunScoring.NeutralArrivalAdvantage,
            RunScoring.ArrivalAdvantage(Observation(new[] { corrupt }), MeetXZ, mySeconds: 1f));
    }

    /// <summary>
    /// The contesting-defender formula itself is deliberately unchanged by AUD-P1 (the defect was the
    /// missing-candidate branch), and this fixture is its no-regression guard.
    ///
    /// Beaten: <c>mySeconds = 3.0</c> ⇒ <c>horizon = 4.0 s</c>; an opponent 10 m from <c>meetXZ</c> needs
    /// ~1.6 s through the reach model, so <c>(1.6 − 3.0) / 1.0</c> clamps to <b>0</b>. Pre-fix: the same 0.
    ///
    /// Arriving about a scale second later: <c>mySeconds = 2.0</c> ⇒ <c>horizon = 3.0 s</c>; this opponent is
    /// 26 m out, which is inside the plausibility filter (<c>26 ≤ 3.0 × 11 = 33 m</c>) but the reach model
    /// cannot cover 26 m less the 1.24 m reach in 3.0 s (~4.5 m acceleration ramp then 8.4 m/s ⇒ ~3.4 s), so
    /// <c>opponentSeconds = horizon = 3.0</c> and <c>(3.0 − 2.0) / 1.0 = 1.0</c>. Pre-fix: the same 1.0.
    /// </summary>
    [Fact]
    public void AIB2_RunValue_ContestingDefender_StillDecidesTheArrivalRace()
    {
        FootballerObservation beaten = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(10f, 0f))
        });
        Assert.Equal(0f, RunScoring.ArrivalAdvantage(beaten, MeetXZ, mySeconds: 3f));

        FootballerObservation late = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(26f, 0f))
        });
        Assert.Equal(1f, RunScoring.ArrivalAdvantage(late, MeetXZ, mySeconds: 2f));
    }

    /// <summary>
    /// The consequence AUD-P1 is really about: the arrival term feeds the advertised run confidence
    /// (<c>RunCandidateGenerator.ConfidenceFrom = 0.20 + 0.55 × arrival + 0.25 × passAvailability</c>, the
    /// documented formula the private method implements), and a promised <c>ToRun</c> then enters
    /// <c>PassSuccessModel.Estimate</c> as its receiver term.
    ///
    /// Fixture: a clear 20 m lane from the carrier (distance inside the 6–32 m useful band ⇒ range 1; no
    /// opponent between the carrier and the meeting point ⇒ lane clearance 1), so
    /// <c>PassAvailability01 = 0.6 × 1 + 0.4 × 1 = 1.0</c> in both pictures.
    /// <list type="bullet">
    /// <item>Uncontested: opponent 40 m out ⇒ arrival 1.0 ⇒ confidence
    ///   <c>0.20 + 0.55 + 0.25 = 1.00</c>.</item>
    /// <item>Contested: opponent 9.3 m off the meeting point ⇒ reach ~1.5 s ⇒ arrival ~0.5 ⇒ confidence
    ///   <c>0.20 + 0.275 + 0.25 = 0.725</c>.</item>
    /// </list>
    /// Pre-fix the uncontested picture also scored 0.5 ⇒ <c>0.20 + 0.275 + 0.25 = 0.725</c>, so the
    /// "&gt; 0.9" assertion fails on the old code. This is the pricing defect the audit measured as an
    /// unmarked run into space losing ~30 % completion probability to a pass to feet.
    /// </summary>
    [Fact]
    public void AIB2_RunValue_UncontestedRunConfidence_ExceedsAContestedOne()
    {
        FootballerObservation uncontested = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(40f, 0f))
        });
        FootballerObservation contested = Observation(new[]
        {
            Entity(25, TeamId.Away, MeetXZ + new Vector2(0f, 9.3f))
        });

        float uncontestedArrival = RunScoring.ArrivalAdvantage(uncontested, MeetXZ, mySeconds: 1f);
        float contestedArrival = RunScoring.ArrivalAdvantage(contested, MeetXZ, mySeconds: 1f);
        float uncontestedPass = RunScoring.PassAvailability(uncontested, CarrierXZ, MeetXZ);
        float contestedPass = RunScoring.PassAvailability(contested, CarrierXZ, MeetXZ);

        // The fixture must isolate the arrival term: the lane and the distance band are identical.
        Assert.Equal(1f, uncontestedPass, 4);
        Assert.Equal(1f, contestedPass, 4);
        Assert.InRange(contestedArrival, 0.2f, 0.8f);

        float uncontestedConfidence = ConfidenceFrom(uncontestedArrival, uncontestedPass);
        float contestedConfidence = ConfidenceFrom(contestedArrival, contestedPass);

        Assert.True(uncontestedConfidence > 0.9f,
            $"an uncontested run must now price above 0.9; it priced {uncontestedConfidence:F3} " +
            $"(arrival {uncontestedArrival:F3}, pass {uncontestedPass:F3}). Pre-fix the 0.5 neutral " +
            "arrival gave 0.725, the same as standing still at feet.");
        Assert.True(contestedConfidence < 0.9f,
            $"a contested run must stay below 0.9; it priced {contestedConfidence:F3} " +
            $"(arrival {contestedArrival:F3}, pass {contestedPass:F3})");
        Assert.True(uncontestedConfidence > contestedConfidence + 0.2f,
            $"the uncontested picture ({uncontestedConfidence:F3}) must outprice the contested one " +
            $"({contestedConfidence:F3}) by the arrival term alone");
    }

    // =============================================================================================
    // AUD-P6 / Fix 5.3 — the width → central-threat blend starts in midfield
    // =============================================================================================

    /// <summary>
    /// The no-build-up-regression half of AUD-P6: at <c>x = 0 m</c> the structure credit is still exactly
    /// the shared AUD-01 width convention. The blend starts at +5 m, so
    /// <c>FinalThirdBlend01(0) = (0 − 5) / 13 &lt; 0 ⇒ 0</c> and the central-threat credit contributes
    /// nothing.
    ///
    /// Pre-fix: identical (the old window also had blend 0 at x = 0); this fixture is the guard that moving
    /// the window earlier did not reprice plain build-up.
    /// </summary>
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 12f)]
    [InlineData(DefaultWidth01, 24f)]
    [InlineData(1f, 30f)]
    public void AIB2_SpaceValue_BuildUpReception_KeepsThePureWidthCredit(
        float attackingWidth01, float lateralMeters)
    {
        ReceptionValueTerms terms = Model.Evaluate(
            Observation(attackingWidth01, Array.Empty<PerceivedEntity>()),
            receiverId: 9,
            arrivalPointXZ: new Vector2(0f, lateralMeters),
            receptionPressure01: 0.2f);

        Assert.Equal(
            AttackingWidthPreference.Score(attackingWidth01, lateralMeters, Pitch),
            terms.Structure01,
            precision: 5);
    }

    /// <summary>
    /// AUD-P6's "genuine blend" requirement at its softest point: at <c>x = +6 m</c> the blend is
    /// <c>(6 − 5) / (18 − 5) = 1/13 ≈ 0.0769</c> — small but nonzero, so the credit has left the pure width
    /// value by at most the blend times the credit gap (the gap is at most 1).
    ///
    /// Fixture arithmetic: width profile 0.82 at |z| = 30 m. Utilisation <c>30/34 = 0.882</c> is below the
    /// profile target <c>0.70 + 0.25 × 0.82 = 0.905</c>, so the width credit is exactly <b>1.0</b>. With no
    /// opponent visible the back-line proximity is the neutral 0.5, so the central threat is
    /// <c>0.60 × (1 − (30 − 12) / 22) + 0.40 × 0.5 = 0.309</c> and the blended credit is
    /// <c>1 + (1/13)(0.309 − 1) = 0.947</c>.
    ///
    /// Pre-fix: the old window began at 17.5 m, so blend(6) = 0 and the structure credit was exactly 1.0 —
    /// the "&lt; width − 0.02" assertion fails on the old code, which is the defect (a midfield reception
    /// was priced on width alone).
    /// </summary>
    [Fact]
    public void AIB2_SpaceValue_MidfieldReception_BlendsOnlySlightly()
    {
        const float lateral = 30f;
        ReceptionValueTerms terms = Model.Evaluate(
            Observation(DefaultWidth01, Array.Empty<PerceivedEntity>()),
            receiverId: 9,
            arrivalPointXZ: new Vector2(6f, lateral),
            receptionPressure01: 0.2f);

        float widthCredit = AttackingWidthPreference.Score(DefaultWidth01, lateral, Pitch);
        float blend = (6f - ReceptionValueModel.FinalThirdBlendStartProjMeters)
                      / (ReceptionValueModel.FinalThirdBlendEndProjMeters
                         - ReceptionValueModel.FinalThirdBlendStartProjMeters);
        float centrality = 1f - (lateral - ReceptionValueModel.CentralCorridorMeters)
                                / (Pitch.HalfWidth - ReceptionValueModel.CentralCorridorMeters);
        float centralThreat = ReceptionValueModel.CentralCorridorWeight * centrality
                              + ReceptionValueModel.BetweenLinesWeight
                              * ReceptionValueModel.UnknownBackLineProximity01;

        Assert.Equal(1f / 13f, blend, 5);
        Assert.Equal(1f, widthCredit, 5);
        Assert.Equal(widthCredit + blend * (centralThreat - widthCredit), terms.Structure01, 4);

        // Pre-fix the blend was 0 here, so this is the assertion that catches the old window.
        Assert.True(terms.Structure01 < widthCredit - 0.02f,
            $"a midfield reception at x = +6 m must have left the pure width credit ({widthCredit:F3}); " +
            $"it scored {terms.Structure01:F3} against a 1/13 blend — pre-fix it scored exactly the " +
            "width credit because the old blend started at 17.5 m");
        Assert.True(MathF.Abs(terms.Structure01 - widthCredit) <= blend + 1e-5f,
            $"at x = +6 m the move off the width credit ({MathF.Abs(terms.Structure01 - widthCredit):F4}) " +
            $"must stay inside the blend ({blend:F4})");
    }

    /// <summary>
    /// AUD-P6's actual claim: by <c>x = +18 m</c> the exchange is complete, so a central reception must
    /// outvalue an equally-pressured touchline one at the same depth for the same profile.
    ///
    /// Fixture: no visible opponents (so turnability, next options and line proximity are profile- and
    /// side-independent), ball at the centre line (same progression), both points 34.5 m+ from goal (no
    /// shooting chance), same pressure. Everything except the structure term is identical.
    ///
    /// Pre-fix arithmetic at this depth: the old window was 17.5 → 25.5 m, so blend(18) =
    /// <c>(18 − 17.5) / 8 = 0.0625</c>; the central structure was <c>0.0625 × 0.80 = 0.050</c> against the
    /// touchline's width credit <b>1.0</b> (|z| = 32 m saturates the width ramp), so the touchline structure
    /// was ~0.95 higher and its value won by <c>0.08 × 0.95 ≈ 0.076</c>. Post-fix blend(18) = 1: the central
    /// structure is <c>0.60 × 1 + 0.40 × 0.5 = 0.80</c> against the touchline's central threat
    /// <c>0.60 × (1 − 20/22) + 0.40 × 0.5 = 0.255</c>, so the central value must be higher. The
    /// <c>central &gt; wide</c> assertion fails on the old code.
    /// </summary>
    [Fact]
    public void AIB2_SpaceValue_FinalThirdCentralReception_OutvaluesTheTouchline()
    {
        FootballerObservation obs = Observation(DefaultWidth01, Array.Empty<PerceivedEntity>());
        ReceptionValueTerms central = Model.Evaluate(obs, 9, new Vector2(18f, 0f), 0.3f);
        ReceptionValueTerms wide = Model.Evaluate(obs, 9, new Vector2(18f, 32f), 0.3f);

        // The blend is complete at +18 m: the credit is the central threat, with the neutral 0.5 back line.
        Assert.Equal(0.80f, central.Structure01, 4);

        // The fixture really does isolate the structure term.
        Assert.Equal(central.Progression01, wide.Progression01, 5);
        Assert.Equal(central.Turnability01, wide.Turnability01, 5);
        Assert.Equal(central.NextOptions01, wide.NextOptions01, 5);
        Assert.Equal(central.ShootingChance01, wide.ShootingChance01, 5);
        Assert.Equal(central.BallSafety01, wide.BallSafety01, 5);

        Assert.True(wide.Structure01 < central.Structure01 - 0.5f,
            $"central structure {central.Structure01:F3} vs touchline {wide.Structure01:F3}");
        Assert.True(central.Value > wide.Value,
            $"a central final-third reception ({central.Value:F3}) must outvalue the equally-pressured " +
            $"touchline one ({wide.Value:F3}); pre-fix the touchline won on raw width at this depth " +
            "(blend 0.0625: structure 0.050 vs 1.0)");
    }

    /// <summary>
    /// The blend must stay a genuine 0..1 blend over the whole pitch and every profile width — no runaway
    /// term, and <see cref="ReceptionValueModel.MaxValue"/> still an upper bound. This passes on the pre-fix
    /// code too (the old blend was also in [0,1]): it is the bounds insurance for moving the window, not the
    /// defect proof.
    /// </summary>
    [Fact]
    public void AIB2_SpaceValue_StructureTerm_StaysBoundedAcrossThePitch()
    {
        float[] widths = { 0f, 0.3f, DefaultWidth01, 1f };
        var visible = new[]
        {
            Entity(14, TeamId.Away, new Vector2(30f, 10f)),
            Entity(15, TeamId.Away, new Vector2(10f, -15f))
        };

        foreach (float width in widths)
        {
            FootballerObservation obs = Observation(width, visible);
            for (float x = -Pitch.HalfLength; x <= Pitch.HalfLength; x += 2.5f)
            {
                for (float z = -Pitch.HalfWidth; z <= Pitch.HalfWidth; z += 4.25f)
                {
                    ReceptionValueTerms terms = Model.Evaluate(obs, 9, new Vector2(x, z), 0.2f);
                    Assert.True(float.IsFinite(terms.Structure01),
                        $"structure is not finite at x = {x:F1}, z = {z:F1}, width {width:F2}");
                    Assert.InRange(terms.Structure01, 0f, 1f);
                    Assert.InRange(terms.Value, 0f, Model.MaxValue);
                }
            }
        }
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    /// <summary>The documented <c>RunCandidateGenerator.ConfidenceFrom</c> formula, restated for the test.</summary>
    private static float ConfidenceFrom(float arrivalAdvantage01, float passAvailability01) =>
        0.20f + 0.55f * arrivalAdvantage01 + 0.25f * passAvailability01;

    /// <summary>
    /// A believed entity in the audit's id convention: ids 14 and above are the away side, so a fixture can
    /// name an opponent inline. Capability is the conservative generic band, as perception supplies it.
    /// </summary>
    private static PerceivedEntity Entity(int id, TeamId team, Vector2 positionXZ) =>
        new(
            id, team, TacticalRole.CentralMidfielder,
            new Vector3(positionXZ.X, 0f, positionXZ.Y),
            Vector3.Zero,
            0.95f);

    private static FootballerObservation Observation(params PerceivedEntity[] visible) =>
        Observation(DefaultWidth01, visible);

    /// <summary>
    /// The observation both models read: the carrier on <see cref="CarrierXZ"/> (ball at his feet, attacking
    /// +x) and the believed players. The run fixtures use it through <c>RunScoring.ArrivalAdvantage</c>, the
    /// reception fixtures through <see cref="ReceptionValueModel.Evaluate"/>.
    /// </summary>
    private static FootballerObservation Observation(
        float attackingWidth01,
        IReadOnlyList<PerceivedEntity> visible) =>
        new()
        {
            SelfId = 8,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CentralMidfielder,
            SelfPosition = new Vector3(CarrierXZ.X, 0f, CarrierXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 1f,
            PerceivedBallPosition = new Vector3(CarrierXZ.X, 0f, CarrierXZ.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(CarrierXZ.X, 0f, CarrierXZ.Y),
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
