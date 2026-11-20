using System;
using FootballSim.Core;

namespace FootballSim.AI;

/// <summary>
/// One archetype's preference deltas (A7.6), as numbers <b>around</b> the neutral baselines.
///
/// Every field is a delta, not a value: <see cref="PlayerArchetypeProfile.Apply"/> adds it to the role's
/// own tendency and clamps the result to 0..1, so the archetype can only lean an existing preference, never
/// replace a role preset or invent a capability. <see cref="None"/> is all zeros, which is what makes
/// <see cref="PlayerTendencyProfile.For"/> with <see cref="PlayerArchetype.None"/> return the pre-A7.6
/// preset bit for bit.
/// </summary>
public readonly record struct ArchetypePreferences(
    float RiskTolerance,
    float DirectPassPreference,
    float ShootingAppetite,
    float DribblePreference,
    float PositionalDiscipline,
    float PressingAggression,
    float CrossPreference,
    float ScanAppetite,
    float SweepAggression)
{
    /// <summary>The neutral delta set: no archetype, no preference.</summary>
    public static ArchetypePreferences None => default;
}

/// <summary>
/// A7.6: <b>archetypes as preferences on one decision pipeline</b>.
///
/// The plan's seven archetypes — playmaker, ball winner, inverted fullback, touchline winger, inside
/// forward, target forward, sweeper keeper — are not seven pipelines and not seven roles. They are three
/// kinds of <i>preference</i>, each applied to a mechanism the player already has:
/// <list type="number">
/// <item><b>candidate weights</b> — the role's <see cref="PlayerTendencies"/> gain bounded affinity deltas
/// (<see cref="Preferences"/>), which the existing scorers already read: a playmaker is more direct and
/// risk-tolerant, an inside forward shoots more and crosses less, a ball winner presses. No scorer gains an
/// archetype branch;</item>
/// <item><b>off-ball candidate weights</b> — <see cref="RunPreference"/> biases the score of the run kinds
/// this player's duty already offers (an inverted fullback underlaps, a target forward pins, a touchline
/// winger holds width). The candidate set is unchanged: the duty decides which kinds are offered, and the
/// archetype only prices them;</item>
/// <item><b>perception preference</b> — <see cref="PlayerTendencies.ScanAppetite01"/> shortens or lengthens
/// the staleness threshold at which perception turns the head to refresh a believed opponent. This is the
/// existing <see cref="ScanPlanner"/> attention policy, not a second sight model.</item>
/// </list>
///
/// The sweeper keeper is the one archetype whose preference lands on the keeper's own duty rather than on a
/// candidate: <see cref="SweepRaceMarginScale"/> widens the sweep/claim race window he accepts and
/// <see cref="SupportDepthScale"/> moves his possession support depth, while every physical reach, area and
/// handling law stays exactly where it was.
///
/// <b>Neutrality.</b> <see cref="PlayerArchetype.None"/> returns zero deltas, zero run preference and scale
/// 1.0 from every helper, so a player without an archetype is decision-for-decision the pre-A7.6 player, and
/// <see cref="AiRuntimeConfig.EnableArchetypes"/> off fields a squad with no archetypes at all.
///
/// <b>What is deliberately not here.</b> An archetype never generates or removes a candidate, never reads an
/// opponent, never widens a sight cone, reach, transmission range or contact rule, and never shares a number
/// on a different scale. The preference values are hand-set calibration (like the A7.2 call preferences) and
/// any change is a behaviour change with a fresh gate.
/// </summary>
public static class PlayerArchetypeProfile
{
    // ---------------------------------------------------------------------------------------------
    // Squad assignment.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The archetype a role is fielded with when the squad has no explicit per-shirt plan (scenario
    /// fixtures and any caller that only states a role). <see cref="TacticalRole.CenterBack"/> is
    /// deliberately <see cref="PlayerArchetype.None"/>: none of the plan's seven archetypes is a centre-back,
    /// and inventing one would be a role change, not a preference.
    /// </summary>
    public static PlayerArchetype DefaultFor(TacticalRole role) => role switch
    {
        TacticalRole.Goalkeeper => PlayerArchetype.SweeperKeeper,
        TacticalRole.FullBack => PlayerArchetype.InvertedFullBack,
        TacticalRole.DefensiveMidfielder => PlayerArchetype.BallWinner,
        TacticalRole.CentralMidfielder => PlayerArchetype.Playmaker,
        TacticalRole.Winger => PlayerArchetype.TouchlineWinger,
        TacticalRole.Striker => PlayerArchetype.TargetForward,
        _ => PlayerArchetype.None
    };

    /// <summary>
    /// The default XI's archetype for one shirt, so the reference match fields <b>all seven</b> archetypes
    /// once each rather than seven copies of a role default: 2 inverts while 3 stays a conventional fullback,
    /// 7 holds the touchline while 11 attacks inside, 8 is the playmaker while 10 is an ordinary midfielder.
    /// A shirt with no archetype among the plan's seven (the centre-backs, shirt 3/10) keeps
    /// <see cref="PlayerArchetype.None"/>. The 5v5 squad maps its three attacking shirts onto the wide and
    /// central roles it actually fields.
    /// </summary>
    public static PlayerArchetype ForShirt(TacticalRole role, int shirtNumber, int playersPerTeam)
    {
        if (playersPerTeam <= 5)
        {
            return shirtNumber switch
            {
                1 => PlayerArchetype.SweeperKeeper,
                3 => PlayerArchetype.TouchlineWinger,
                4 => PlayerArchetype.InsideForward,
                5 => PlayerArchetype.TargetForward,
                _ => DefaultFor(role)
            };
        }

        return shirtNumber switch
        {
            1 => PlayerArchetype.SweeperKeeper,
            2 => PlayerArchetype.InvertedFullBack,
            6 => PlayerArchetype.BallWinner,
            7 => PlayerArchetype.TouchlineWinger,
            8 => PlayerArchetype.Playmaker,
            9 => PlayerArchetype.TargetForward,
            11 => PlayerArchetype.InsideForward,
            _ => PlayerArchetype.None
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Candidate weights: the role's tendencies gain the archetype's deltas.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The archetype's tendency deltas. Zero for <see cref="PlayerArchetype.None"/>.</summary>
    public static ArchetypePreferences Preferences(PlayerArchetype archetype) => archetype switch
    {
        // Wants the ball in the middle and plays it forward: more direct, more risk, scans more; shoots and
        // dribbles less than a neutral midfielder.
        PlayerArchetype.Playmaker => new ArchetypePreferences(
            RiskTolerance: 0.05f,
            DirectPassPreference: 0.10f,
            ShootingAppetite: -0.10f,
            DribblePreference: -0.05f,
            PositionalDiscipline: 0f,
            PressingAggression: -0.05f,
            CrossPreference: 0.05f,
            ScanAppetite: 0.25f,
            SweepAggression: 0f),

        // Wins it back and gives it simply: the most aggressive presser in the side, disciplined, and the
        // least likely to shoot or dribble.
        PlayerArchetype.BallWinner => new ArchetypePreferences(
            RiskTolerance: 0.02f,
            DirectPassPreference: -0.08f,
            ShootingAppetite: -0.10f,
            DribblePreference: -0.10f,
            PositionalDiscipline: 0.05f,
            PressingAggression: 0.20f,
            CrossPreference: -0.10f,
            ScanAppetite: 0.15f,
            SweepAggression: 0f),

        // A fullback who steps inside: comfortable on the ball and licensed to leave the line, but less of a
        // crossing threat, because the underlap is his route.
        PlayerArchetype.InvertedFullBack => new ArchetypePreferences(
            RiskTolerance: 0.03f,
            DirectPassPreference: 0.05f,
            ShootingAppetite: -0.02f,
            DribblePreference: 0.10f,
            PositionalDiscipline: -0.10f,
            PressingAggression: 0f,
            CrossPreference: -0.05f,
            ScanAppetite: 0.05f,
            SweepAggression: 0f),

        // Stays wide and delivers: the highest cross appetite in the side, a take-on, and less shooting than
        // the inside forward beside him.
        PlayerArchetype.TouchlineWinger => new ArchetypePreferences(
            RiskTolerance: 0.05f,
            DirectPassPreference: 0.05f,
            ShootingAppetite: -0.05f,
            DribblePreference: 0.12f,
            PositionalDiscipline: -0.05f,
            PressingAggression: 0f,
            CrossPreference: 0.20f,
            ScanAppetite: 0f,
            SweepAggression: 0f),

        // Attacks the half-space and the box: shoots and drives inside, and is the wide player who does not
        // deliver the ball.
        PlayerArchetype.InsideForward => new ArchetypePreferences(
            RiskTolerance: 0.05f,
            DirectPassPreference: 0.05f,
            ShootingAppetite: 0.12f,
            DribblePreference: 0.10f,
            PositionalDiscipline: -0.08f,
            PressingAggression: -0.02f,
            CrossPreference: -0.15f,
            ScanAppetite: 0f,
            SweepAggression: 0f),

        // Holds the last line and the box: a target, not a dribbler — disciplined, aerial-arrival oriented,
        // and less likely to drop in to combine.
        PlayerArchetype.TargetForward => new ArchetypePreferences(
            RiskTolerance: 0f,
            DirectPassPreference: -0.05f,
            ShootingAppetite: 0.08f,
            DribblePreference: -0.12f,
            PositionalDiscipline: 0.04f,
            PressingAggression: -0.05f,
            CrossPreference: 0.05f,
            ScanAppetite: 0f,
            SweepAggression: 0f),

        // Plays high behind the line: a much bolder sweep window and a deeper support position, plus a keeper
        // who scans more because he is a defender of space, not only of his line.
        PlayerArchetype.SweeperKeeper => new ArchetypePreferences(
            RiskTolerance: 0.05f,
            DirectPassPreference: 0.05f,
            ShootingAppetite: -0.05f,
            DribblePreference: 0f,
            PositionalDiscipline: -0.05f,
            PressingAggression: 0f,
            CrossPreference: 0f,
            ScanAppetite: 0.10f,
            SweepAggression: 0.30f),

        _ => ArchetypePreferences.None
    };

    /// <summary>
    /// Applies one archetype's deltas to a role preset and clamps every field to 0..1. With
    /// <see cref="PlayerArchetype.None"/> this returns a preset whose values are identical to the input's
    /// (a fresh object, so a caller cannot tell the difference by behaviour).
    /// </summary>
    public static PlayerTendencies Apply(PlayerTendencies tendencies, PlayerArchetype archetype)
    {
        ArgumentNullException.ThrowIfNull(tendencies);

        ArchetypePreferences delta = Preferences(archetype);
        if (archetype == PlayerArchetype.None) return tendencies;

        return new PlayerTendencies
        {
            RiskTolerance01 = Clamp01(tendencies.RiskTolerance01 + delta.RiskTolerance),
            DirectPassPreference01 = Clamp01(tendencies.DirectPassPreference01 + delta.DirectPassPreference),
            ShootingAppetite01 = Clamp01(tendencies.ShootingAppetite01 + delta.ShootingAppetite),
            DribblePreference01 = Clamp01(tendencies.DribblePreference01 + delta.DribblePreference),
            PositionalDiscipline01 = Clamp01(tendencies.PositionalDiscipline01 + delta.PositionalDiscipline),
            PressingAggression01 = Clamp01(tendencies.PressingAggression01 + delta.PressingAggression),
            CrossPreference01 = Clamp01(tendencies.CrossPreference01 + delta.CrossPreference),
            ScanAppetite01 = Clamp01(tendencies.ScanAppetite01 + delta.ScanAppetite),
            SweepAggression01 = Clamp01(tendencies.SweepAggression01 + delta.SweepAggression)
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Off-ball weights: a preference on the run kinds the duty already offers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// How much more (positive) or less (negative) this archetype wants one off-ball path, in [-1, 1]. It is
    /// a preference, not a licence: a duty that never offers a kind still never offers it, and the generator's
    /// kind list is untouched.
    /// </summary>
    public static float RunPreference(RunKind kind, PlayerArchetype archetype)
    {
        if (archetype == PlayerArchetype.None) return 0f;

        return archetype switch
        {
            PlayerArchetype.Playmaker => kind switch
            {
                RunKind.CheckShort => 0.5f,
                RunKind.RecycleSupport => 0.4f,
                RunKind.InBehind => -0.2f,
                RunKind.Overlap => -0.2f,
                _ => 0f
            },
            PlayerArchetype.BallWinner => kind switch
            {
                RunKind.RecycleSupport => 0.4f,
                RunKind.CheckShort => 0.2f,
                RunKind.InBehind => -0.3f,
                RunKind.Overlap => -0.3f,
                RunKind.LateBoxArrival => -0.3f,
                _ => 0f
            },
            PlayerArchetype.InvertedFullBack => kind switch
            {
                RunKind.Underlap => 0.8f,
                RunKind.RecycleSupport => 0.2f,
                RunKind.CheckShort => 0.1f,
                RunKind.Overlap => -0.5f,
                RunKind.HoldWidth => -0.3f,
                _ => 0f
            },
            PlayerArchetype.TouchlineWinger => kind switch
            {
                RunKind.HoldWidth => 0.9f,
                RunKind.Overlap => 0.3f,
                RunKind.InBehind => 0.2f,
                RunKind.Underlap => -0.2f,
                _ => 0f
            },
            PlayerArchetype.InsideForward => kind switch
            {
                RunKind.InBehind => 0.5f,
                RunKind.LateBoxArrival => 0.5f,
                RunKind.Underlap => 0.4f,
                RunKind.BlindSideArrival => 0.3f,
                RunKind.HoldWidth => -0.5f,
                RunKind.Overlap => -0.2f,
                _ => 0f
            },
            PlayerArchetype.TargetForward => kind switch
            {
                RunKind.Pin => 0.8f,
                RunKind.LateBoxArrival => 0.4f,
                RunKind.BlindSideArrival => 0.4f,
                RunKind.Overlap => -0.5f,
                RunKind.Underlap => -0.5f,
                RunKind.HoldWidth => -0.2f,
                RunKind.CheckShort => -0.2f,
                _ => 0f
            },
            _ => 0f
        };
    }

    /// <summary>
    /// What one full unit of <see cref="RunPreference"/> is worth in the run score's units. Small on purpose:
    /// the six receiving terms sum to at most 0.60 and the role term is worth 0.10, so a preference must
    /// nudge a choice between two comparable paths without outbidding the geometry of the run itself.
    /// </summary>
    public const float RunPreferenceWeight = 0.05f;

    // ---------------------------------------------------------------------------------------------
    // The sweeper keeper's preference on the keeper's own duty.
    //
    // These scale numbers the keeper already owns; they do not add reach. `1.0` is the neutral keeper, so a
    // non-sweeper keeper (or archetypes off) keeps the pre-A7.6 goalkeeper decision exactly.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The "contested" window of the sweep and claim race, as a share of the neutral value: a bolder keeper
    /// accepts a race he is slightly behind in. 1.0 at the neutral tendency.
    ///
    /// This is the sweeper keeper's decision-side preference. Only the <i>window</i> moves: the sweep's
    /// commit range, every reach, the area and the handling law are the neutral keeper's. (Scaling the commit
    /// range was tried and is inert — the two-second trajectory horizon and the keeper's own acceleration
    /// already bound how far he can reach, so a wider committed radius accepts nothing new. The contest
    /// margin is the lever that actually changes a decision.)
    /// </summary>
    public static float SweepRaceMarginScale(float sweepAggression01) =>
        0.60f + 0.80f * Clamp01(sweepAggression01); // 0.50 neutral -> 1.00; 0.80 sweeper -> 1.24

    /// <summary>
    /// The possession support depth, as a share of the neutral value: a sweeper keeper stands further off his
    /// line as an outlet. 1.0 at the neutral tendency. The penalty-area cap is applied after this scale, so a
    /// sweeper keeper is still a keeper.
    /// </summary>
    public static float SupportDepthScale(float sweepAggression01) =>
        0.50f + 1.00f * Clamp01(sweepAggression01); // 0.50 neutral -> 1.00; 0.80 sweeper -> 1.30

    private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
}
