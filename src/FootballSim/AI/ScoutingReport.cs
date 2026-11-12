using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// What one player believes another player is physically capable of (A2.3): a coarse band, not the
/// opponent's live <see cref="PlayerAttributes"/>.
///
/// The distinction matters even though attributes are static in this simulation. A scorer that reads
/// <c>opponent.Attributes.MaxSprintSpeed</c> is reading the world, and the next attribute anyone adds
/// (composure, first touch, a tendency, an intention) would leak through the same door. So the estimate
/// is a separate object with its own confidence, filled either from a scouting report or from a
/// conservative generic default — and <see cref="PerceivedEntity.Capability"/> is the only way a scorer
/// can reach it.
/// </summary>
public readonly record struct CapabilityEstimate(
    float MaxSprintSpeed,
    float Acceleration,
    float AgilityTurnRate,
    float ReachRadius,
    float Confidence01)
{
    /// <summary>
    /// The conservative default used when nobody has scouted this player: the neutral
    /// <see cref="PlayerAttributes"/> values, which is what the pass-lane test already assumed. It is
    /// deliberately *not* optimistic about anyone, and it carries zero confidence so a caller can tell
    /// "assumed" from "known".
    /// </summary>
    public static CapabilityEstimate Generic => new(
        MaxSprintSpeed: 8.4f,
        Acceleration: 6.8f,
        AgilityTurnRate: 9.5f,
        ReachRadius: 0.75f,
        Confidence01: 0f);

    /// <summary>True when this came from a scouting report rather than the generic assumption.</summary>
    public bool IsScouted => Confidence01 > 0f;

    /// <summary>
    /// Reach kinematics of this player, as believed. The position and velocity are the observer's
    /// estimates; only the body limits come from the capability band.
    /// </summary>
    public ReachKinematics ToKinematics(Vector2 positionXZ, Vector2 velocityXZ, bool goalkeeper = false) =>
        new(
            positionXZ,
            velocityXZ,
            SimMath.SafeNormalize(velocityXZ, new Vector2(1f, 0f)),
            MaxSprintSpeed,
            Acceleration,
            AgilityTurnRate,
            Balance01: 1f,
            Stamina01: 1f,
            BallContactRules.ReachRadius(
                ReachRadius, goalkeeper, isDiving: false, requestingTackle: false, receivingOrDribbling: true));
}

/// <summary>
/// Pre-match scouting knowledge of a squad's physical capability (A2.3).
///
/// A football team arrives knowing roughly how quick the opposing winger is; it does not know where he
/// will run in the next half second. This report is that first kind of knowledge: built once from the
/// squad at kickoff, quantised into bands and carrying a confidence, so a scorer receives an *estimate*
/// with an error band rather than a live read of the authoritative attribute.
///
/// The banding is not decoration — it is the reason a test can tell the two apart. If a scorer ever read
/// the live attribute it would also read values that are not multiples of the band.
/// </summary>
public sealed class ScoutingReport
{
    /// <summary>Speed is reported to this resolution, in metres per second.</summary>
    public const float SpeedBand = 0.25f;

    /// <summary>Acceleration is reported to this resolution, in metres per second squared.</summary>
    public const float AccelerationBand = 0.5f;

    /// <summary>Turn rate is reported to this resolution, in radians per second.</summary>
    public const float TurnRateBand = 0.5f;

    /// <summary>Reach is reported to this resolution, in metres.</summary>
    public const float ReachBand = 0.05f;

    /// <summary>How much a scouted value is trusted. Scouting is useful, not omniscient.</summary>
    public const float ScoutingConfidence01 = 0.8f;

    private readonly Dictionary<int, CapabilityEstimate> _estimates = new();

    /// <summary>
    /// Builds the report for a squad. Callers pass the players once, at kickoff; nothing here is
    /// refreshed during play, which is what makes it scouting rather than a live view.
    /// </summary>
    public static ScoutingReport FromSquad(IReadOnlyList<FootballerState> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        var report = new ScoutingReport();
        foreach (FootballerState player in players)
        {
            PlayerAttributes attributes = player.Attributes;
            report._estimates[player.Id] = new CapabilityEstimate(
                Quantise(attributes.MaxSprintSpeed, SpeedBand),
                Quantise(attributes.Acceleration, AccelerationBand),
                Quantise(attributes.AgilityTurnRate, TurnRateBand),
                Quantise(attributes.ReachRadius, ReachBand),
                ScoutingConfidence01);
        }
        return report;
    }

    /// <summary>The scouted estimate for a player, or the generic assumption when they are not in it.</summary>
    public CapabilityEstimate Get(int playerId) =>
        _estimates.TryGetValue(playerId, out CapabilityEstimate estimate) ? estimate : CapabilityEstimate.Generic;

    /// <summary>Number of players this report covers; diagnostics and tests read it.</summary>
    public int Count => _estimates.Count;

    /// <summary>Rounds a value to its band, so a scouted number is never the exact live one.</summary>
    private static float Quantise(float value, float band) =>
        float.IsFinite(value) ? MathF.Round(value / band) * band : 0f;
}
