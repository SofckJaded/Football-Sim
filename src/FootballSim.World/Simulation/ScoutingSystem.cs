using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>
/// One player a buyer's scouts recommend: who he is, who he plays for, how well he fits the buyer's need
/// and how visible he is to the market.
/// </summary>
/// <param name="PlayerId">The recommended player.</param>
/// <param name="ClubId">The player's current club (never <see cref="ClubId.None"/>; free agents are signed
/// directly by the transfer market, not through scouting).</param>
/// <param name="Score">Combined recruitment score; higher is better.</param>
/// <param name="Visibility">The player's scouting visibility at the time of the report, 0..1.</param>
public sealed record ScoutingTarget(PlayerId PlayerId, ClubId ClubId, float Score, float Visibility);

/// <summary>
/// Scouting and player visibility (plan §5.4 "Scouting &amp; Visibility", stage W3).
///
/// <para><b>Model.</b> Every player has a <see cref="WorldPlayer.Visibility01"/> — how visible he is to
/// clubs above him. It is a slow signal: each week it moves a few percent toward a target set by the
/// player's division, his backstory exposure and his form, plus a small performance bonus from this season's
/// goals and assists and a tiny deterministic jitter. A cup giant-killing is the one fast jump (the plan's
/// 2.5x spotlight), applied at the rollover by <see cref="ApplyCupGiantKillingSpikes"/>.</para>
///
/// <para><b>Who uses it.</b> <see cref="FindTargets"/> turns visibility plus positional fit into the
/// shortlist the transfer market works through: a buyer only sees players above its division's scouting
/// threshold, prefers players from its own or lower divisions, and ranks by a blend of area fit, visibility
/// and form minus an age penalty.</para>
///
/// <para><b>Determinism.</b> <see cref="AdvanceWeek"/> walks the player registry in stable order and takes
/// exactly one jitter draw per live player; <see cref="FindTargets"/> allocates no randomness at all. Both
/// mutate nothing except the visibility values the week tick owns.</para>
/// </summary>
public static class ScoutingSystem
{
    /// <summary>
    /// The plan's giant-killing spotlight: a cup win over a club at least two divisions higher multiplies
    /// every winning squad member's visibility by this factor (clamped at 1.0).
    /// </summary>
    public const float GiantKillingVisibilityMultiplier = 2.5f;

    /// <summary>Scouting threshold base: <c>0.60 - 0.06 * tier</c> (tier 1 ≈ 0.54 .. tier 6 ≈ 0.24).</summary>
    private const float ThresholdBase = 0.60f;

    /// <summary>How much each division lowers the scouting threshold.</summary>
    private const float ThresholdPerTier = 0.06f;

    /// <summary>Fraction of the gap to the target a player's visibility closes each week.</summary>
    private const float WeeklyMoveRate = 0.06f;

    /// <summary>Performance bonus per goal this season.</summary>
    private const float GoalVisibilityBonus = 0.015f;

    /// <summary>Performance bonus per assist this season.</summary>
    private const float AssistVisibilityBonus = 0.008f;

    /// <summary>Cap on the weekly performance bonus (so one hat-trick cannot make a player a household name).</summary>
    private const float MaximumPerformanceBonus = 0.05f;

    /// <summary>Magnitude of the symmetric weekly jitter draw.</summary>
    private const float WeeklyJitter = 0.004f;

    /// <summary>Visibility target a player with no club slowly decays toward.</summary>
    private const float FreeAgentVisibilityTarget = 0.15f;

    /// <summary>Lowest target the tier/backstory/form formula may produce (plan's 0.05 floor).</summary>
    private const float MinimumTargetVisibility = 0.05f;

    /// <summary>Highest target the tier/backstory/form formula may produce (plan's 0.98 ceiling).</summary>
    private const float MaximumTargetVisibility = 0.98f;

    /// <summary>Oldest player a scout will shortlist (a stop-gap signing, not a rebuild).</summary>
    private const int MaximumTargetAge = 32;

    /// <summary>Age beyond which every extra year subtracts from a target's recruitment score.</summary>
    private const int AgePenaltyStartAge = 29;

    /// <summary>Score penalty per year over <see cref="AgePenaltyStartAge"/>.</summary>
    private const float AgePenaltyPerYear = 0.02f;

    /// <summary>
    /// Baseline visibility of a player in each division, indexed by tier (index 0 unused). Top-flight
    /// players are known nationwide; amateur players are almost invisible outside their own town.
    /// </summary>
    private static readonly float[] TierBaselineVisibility = { 0.0f, 0.55f, 0.45f, 0.35f, 0.25f, 0.16f, 0.10f };

    /// <summary>Tier count of the v1 world (also the valid tier range).</summary>
    private const int TierCount = 6;

    /// <summary>
    /// Baseline visibility of a division: tier 1 0.55, tier 2 0.45, tier 3 0.35, tier 4 0.25, tier 5 0.16,
    /// tier 6 0.10.
    /// </summary>
    /// <param name="tier">Division tier 1..6.</param>
    /// <returns>The division's baseline visibility, 0..1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is outside 1..6.</exception>
    public static float VisibilityBaselineForTier(int tier)
    {
        if (tier is < 1 or > TierCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), tier, "Tier must be in 1..6.");
        }

        return TierBaselineVisibility[tier];
    }

    /// <summary>
    /// Backstory exposure multiplier (plan §4.4): an elite academy graduate is scouted far earlier than a
    /// street footballer of the same talent. EliteAcademy 1.45, RegionalAcademy 1.15, LocalClubYouth 0.75,
    /// StreetFootball 0.60, MultisportLateStarter 0.70.
    /// </summary>
    /// <param name="backstory">Where the player came from.</param>
    /// <returns>The multiplier applied to the division baseline.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backstory"/> is not a defined value.</exception>
    public static float ExposureMultiplier(BackstoryKind backstory) => backstory switch
    {
        BackstoryKind.EliteAcademy => 1.45f,
        BackstoryKind.RegionalAcademy => 1.15f,
        BackstoryKind.LocalClubYouth => 0.75f,
        BackstoryKind.StreetFootball => 0.60f,
        BackstoryKind.MultisportLateStarter => 0.70f,
        _ => throw new ArgumentOutOfRangeException(nameof(backstory), backstory, "Unknown backstory.")
    };

    /// <summary>
    /// Advances every player's visibility one week (plan §5.4).
    ///
    /// <para>For a player with a club, the target is
    /// <c>clamp(tierBaseline * exposure * (0.85 + 0.30 * form), 0.05, 0.98)</c> where form is the rolling
    /// recent-form rating normalised to 0..1 (0.5 with no appearances). Visibility then moves
    /// <c>6%</c> of the way toward that target, gains
    /// <c>min(0.05, 0.015 * goals + 0.008 * assists)</c> from this season's output, and receives a symmetric
    /// <c>±0.004</c> jitter from <paramref name="rng"/>; the result is clamped to 0..1. A free agent has no
    /// club to platform him and slowly decays toward 0.15. Retired players are skipped.</para>
    /// </summary>
    /// <param name="world">The world whose players are updated.</param>
    /// <param name="rng">Scouting stream; exactly one jitter draw per live player, in registry order.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public static void AdvanceWeek(WorldState world, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(rng);

        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;

            bool hasClub = player.ClubId.IsValid &&
                world.Clubs.TryGet(player.ClubId, out WorldClub? club) && club is not null;

            float target;
            float performanceBonus;
            if (hasClub)
            {
                int tier = Math.Clamp(world.Clubs[player.ClubId].Tier, 1, TierCount);
                float form = player.Form.Form01();
                target = TierBaselineVisibility[tier] * ExposureMultiplier(player.Backstory) *
                    (0.85f + 0.30f * form);
                target = Math.Clamp(target, MinimumTargetVisibility, MaximumTargetVisibility);
                performanceBonus = PerformanceBonus(player);
            }
            else
            {
                target = FreeAgentVisibilityTarget;
                performanceBonus = 0f;
            }

            float visibility = player.Visibility01;
            visibility += (target - visibility) * WeeklyMoveRate + performanceBonus +
                rng.NextRange(-WeeklyJitter, WeeklyJitter);
            player.Visibility01 = Math.Clamp(visibility, 0f, 1f);
        }
    }

    /// <summary>
    /// Applies the plan's cup giant-killing spotlight at the rollover: for every played cup tie this season
    /// in which the winner plays at least two divisions below the loser, every player in the winning squad
    /// gets his visibility multiplied by <see cref="GiantKillingVisibilityMultiplier"/> (clamped at 1.0).
    ///
    /// <para>The condition compares division numbers, so it fires only when the winner's tier is
    /// <c>loser.Tier + 2</c> or worse — a tier-6 side beating a tier-4 or tier-1 side, never a narrow
    /// one-division upset. The spike is applied once per call; the rollover calls it once per season.</para>
    /// </summary>
    /// <param name="world">The world whose cup results and player visibility are read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    public static void ApplyCupGiantKillingSpikes(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);

        foreach (CupRoundBracket round in world.Cup.Rounds)
        {
            foreach (CupTie tie in round.Ties)
            {
                WorldFixture fixture = tie.Fixture;
                if (!fixture.IsPlayed) continue;

                ClubId winnerClubId = fixture.CupWinner;
                ClubId loserClubId = fixture.CupLoser;
                if (!winnerClubId.IsValid || !loserClubId.IsValid) continue;

                if (!world.Clubs.TryGet(winnerClubId, out WorldClub? winnerClub) || winnerClub is null) continue;
                if (!world.Clubs.TryGet(loserClubId, out WorldClub? loserClub) || loserClub is null) continue;
                if (winnerClub.Tier < loserClub.Tier + 2) continue;

                IReadOnlyList<PlayerId> squad = winnerClub.Squad;
                for (int i = 0; i < squad.Count; i++)
                {
                    if (!world.Players.TryGet(squad[i], out WorldPlayer? player) || player is null) continue;
                    if (player.IsRetired) continue;
                    player.Visibility01 = Math.Clamp(
                        player.Visibility01 * GiantKillingVisibilityMultiplier, 0f, 1f);
                }
            }
        }
    }

    /// <summary>
    /// Builds a buyer's scouting shortlist from every player in the world (plan §5.4 "higher-tier clubs scan
    /// lower-tier players whose visibility passes their scouting threshold").
    ///
    /// <para><b>Candidates</b>: players at another club, not retired, not on loan, aged at most 32, playing in
    /// the buyer's division or below (equal or lower tiers, never a club above the buyer), whose visibility
    /// strictly exceeds the buyer's threshold <c>0.60 - 0.06 * tier</c>.</para>
    ///
    /// <para><b>Score</b>: <c>0.50 * AreaScore(player, need) / 100 + 0.30 * visibility + 0.20 * form</c>
    /// minus 0.02 per year over 29, where <c>need</c> is the buyer's
    /// <see cref="RosterAnalyser.FindShortfall"/> area (or Forward when the squad has no shortfall). The list
    /// is sorted by score descending, ties by lower <see cref="PlayerId"/> first, and truncated to
    /// <paramref name="maxTargets"/> using a bounded insertion so a full scan allocates nothing per
    /// candidate. No randomness is consumed.</para>
    /// </summary>
    /// <param name="world">The world to scan.</param>
    /// <param name="buyer">The club doing the scouting.</param>
    /// <param name="maxTargets">Maximum shortlist size (must be positive to return anything).</param>
    /// <returns>The ranked shortlist; empty when nothing passes the threshold.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> or <paramref name="buyer"/> is null.</exception>
    public static IReadOnlyList<ScoutingTarget> FindTargets(
        WorldState world, WorldClub buyer, int maxTargets = 12)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(buyer);

        if (maxTargets <= 0) return Array.Empty<ScoutingTarget>();

        RosterGroup area = RosterAnalyser.FindShortfall(world, buyer) ?? RosterGroup.Forward;
        float threshold = ThresholdBase - ThresholdPerTier * Math.Clamp(buyer.Tier, 1, TierCount);
        int buyerTier = Math.Clamp(buyer.Tier, 1, TierCount);

        var shortlist = new List<ScoutingTarget>(maxTargets);

        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            if (player.Contract.IsOnLoan) continue;
            if (player.Age > MaximumTargetAge) continue;
            if (!player.ClubId.IsValid || player.ClubId == buyer.Id) continue;
            if (!world.Clubs.TryGet(player.ClubId, out WorldClub? club) || club is null) continue;
            if (club.Id == buyer.Id) continue;
            if (club.Tier < buyerTier) continue;
            if (player.Visibility01 <= threshold) continue;

            float score = 0.50f * (RosterAnalyser.AreaScore(player, area) / 100f) +
                0.30f * player.Visibility01 +
                0.20f * player.Form.Form01();
            if (player.Age > AgePenaltyStartAge)
            {
                score -= AgePenaltyPerYear * (player.Age - AgePenaltyStartAge);
            }

            InsertRanked(shortlist, player.Id, club.Id, score, player.Visibility01, maxTargets);
        }

        return shortlist;
    }

    // ---------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>This season's goal/assist visibility bonus, capped at <see cref="MaximumPerformanceBonus"/>.</summary>
    private static float PerformanceBonus(WorldPlayer player)
    {
        PlayerSeasonStats stats = player.CurrentSeason;
        float bonus = GoalVisibilityBonus * stats.Goals + AssistVisibilityBonus * stats.Assists;
        return Math.Min(bonus, MaximumPerformanceBonus);
    }

    /// <summary>
    /// Inserts a candidate into the bounded, score-ordered shortlist. The list stays sorted by score
    /// descending and then player id ascending; once full, a candidate that does not beat the worst entry is
    /// dropped. The score is compared before a <see cref="ScoutingTarget"/> is created, so scanning a world
    /// of thousands of players allocates only the entries that make the shortlist.
    /// </summary>
    private static void InsertRanked(
        List<ScoutingTarget> shortlist,
        PlayerId playerId,
        ClubId clubId,
        float score,
        float visibility,
        int capacity)
    {
        int index = 0;
        while (index < shortlist.Count && IsRankedBefore(shortlist[index], score, playerId)) index++;

        if (index >= capacity) return;
        if (shortlist.Count >= capacity) shortlist.RemoveAt(shortlist.Count - 1);

        shortlist.Insert(index, new ScoutingTarget(playerId, clubId, score, visibility));
    }

    /// <summary>True when an existing entry ranks before a candidate (score descending, then player id ascending).</summary>
    private static bool IsRankedBefore(ScoutingTarget existing, float score, PlayerId playerId)
    {
        if (existing.Score != score) return existing.Score > score;
        return existing.PlayerId.Value < playerId.Value;
    }
}
