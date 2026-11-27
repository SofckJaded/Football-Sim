using System;
using System.Collections.Generic;
using FootballSim.Core;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>One manager change produced by the end-of-season carousel.</summary>
/// <param name="ClubId">The club that changed manager.</param>
/// <param name="OutgoingManagerId">The manager who left the post.</param>
/// <param name="IncomingManagerId">The newly hired manager.</param>
/// <param name="WasSacked">True when the board dismissed the outgoing manager (the only carousel path in v1).</param>
public sealed record ManagerChangeRecord(
    ClubId ClubId,
    ManagerId OutgoingManagerId,
    ManagerId IncomingManagerId,
    bool WasSacked);

/// <summary>What one end-of-season manager carousel produced.</summary>
/// <param name="Sacked">Number of managers dismissed.</param>
/// <param name="Hired">Number of replacement managers hired.</param>
/// <param name="UnemployedAfter">Managers with no club after the procedure.</param>
/// <param name="Changes">Every change, in club-id order.</param>
public sealed record ManagerCarouselResult(
    int Sacked,
    int Hired,
    int UnemployedAfter,
    IReadOnlyList<ManagerChangeRecord> Changes);

/// <summary>
/// The end-of-season manager carousel (plan §5.2, stage W3): it converts each club's final league position into
/// a board-confidence verdict, dismisses the managers whose season fell short of the division's expectation and
/// hires deterministic replacements.
///
/// <para><b>Expectation model.</b> A club's expectation has two halves, exactly as the plan states: its points
/// per game against the division's mid-table expectation (<see cref="ExpectedPointsPerGame"/>), and its final
/// position against the position its reputation predicts inside the division it played in (1 = strongest
/// reputation). The two halves are weighted 0.5 each around the neutral confidence 0.35 and clamped to
/// 0.05..0.95, so a board can never be at a literal 0 or 1.</para>
///
/// <para><b>Determinism.</b> Clubs are processed in club-id order (the registry's stable order). The
/// 0.04 under-performance roll is drawn only for a club that is not already sacked by the confidence or
/// positional rule and underperformed its expected position by three or more places, so the rng stream is a
/// pure function of the world state. A replacement is built from consecutive rng draws in a documented order
/// (name pair, age, reputation, tactical preset), so the same seed always produces the same new manager.</para>
/// </summary>
public static class ManagerCarousel
{
    // ---------------------------------------------------------------------------------------------
    // Expectation tables.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The division's mid-table expectation, in points per game, indexed by tier (index 0 unused):
    /// Tier 1 1.45 down to Tier 6 1.15. A mid-table side in a stronger division is expected to collect
    /// slightly more points, because the weaker clubs in it are still relatively strong.
    /// </summary>
    private static readonly float[] ExpectedPointsPerGameByTier = { 0f, 1.45f, 1.35f, 1.30f, 1.25f, 1.20f, 1.15f };

    /// <summary>Manager reputation bands per tier, inclusive: a Tier-1 board hires 55..85, Tier 6 hires 12..35.</summary>
    private static readonly int[] ManagerReputationMinimumByTier = { 0, 55, 45, 35, 25, 18, 12 };
    private static readonly int[] ManagerReputationMaximumByTier = { 0, 85, 75, 65, 55, 45, 35 };

    /// <summary>Number of tactical presets a new manager is picked from.</summary>
    private const int TacticalPresetCount = 6;

    /// <summary>Preset index of possession control; it and high press line up 4-3-3 (plan §5.1).</summary>
    private const int PresetPossessionControl = 0;
    private const int PresetHighPress = 3;

    // ---------------------------------------------------------------------------------------------
    // Board-confidence formula.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Neutral board confidence a club performing exactly to expectation starts from.</summary>
    private const float NeutralBoardConfidence = 0.35f;

    /// <summary>Board confidence is clamped into this band so no manager is literally safe or doomed.</summary>
    private const float MinimumBoardConfidence = 0.05f;
    private const float MaximumBoardConfidence = 0.95f;

    /// <summary>Scale of the points-per-game half of the verdict (1.5 points per game = full weight).</summary>
    private const float PointsPerGameScale = 1.5f;

    /// <summary>Scale of the position half of the verdict (10 places = full weight).</summary>
    private const float PositionScale = 10f;

    /// <summary>The two halves of the verdict are weighted equally.</summary>
    private const float PointsWeight = 0.5f;
    private const float PositionWeight = 0.5f;

    // ---------------------------------------------------------------------------------------------
    // Sack rules.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A board confidence below this dismisses the manager outright.</summary>
    private const float SackConfidenceThreshold = 0.30f;

    /// <summary>Finishing this many places below expectation always dismisses a struggling club's manager.</summary>
    private const int CatastrophicPositionGap = 6;

    /// <summary>"Finished below 14th": the positional sack rule only applies to positions 15..18.</summary>
    private const int LastRespectablePosition = 14;

    /// <summary>A club three or more places below expectation can still lose its manager on the roll.</summary>
    private const int RandomSackUnderperformanceGap = 3;

    /// <summary>Chance of that random dismissal.</summary>
    private const float RandomSackChance = 0.04f;

    // ---------------------------------------------------------------------------------------------
    // Replacement-manager constants.
    // ---------------------------------------------------------------------------------------------

    /// <summary>New-manager age range (inclusive).</summary>
    private const int MinimumManagerAge = 35;
    private const int MaximumManagerAge = 65;

    /// <summary>New managers start the board off on this confidence.</summary>
    private const float NewManagerBoardConfidence = 0.60f;

    /// <summary>Name-pair draws tried before a name that differs from the outgoing manager's is accepted.</summary>
    private const int MaximumNameAttempts = 25;

    // ---------------------------------------------------------------------------------------------
    // Public API.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The division's mid-table points-per-game expectation: Tier 1 1.45, Tier 2 1.35, Tier 3 1.30,
    /// Tier 4 1.25, Tier 5 1.20, Tier 6 1.15.
    /// </summary>
    /// <param name="tier">Division tier 1..6.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tier"/> is outside 1..6.</exception>
    public static float ExpectedPointsPerGame(int tier)
    {
        if (tier is < 1 or > 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tier), tier, "Division tier must be between 1 and 6.");
        }

        return ExpectedPointsPerGameByTier[tier];
    }

    /// <summary>
    /// End-of-season board review. For every club, in club-id order: reads the final row/position from
    /// <paramref name="outcome"/>, computes the expectation (division points-per-game target and the position
    /// its reputation predicts inside its division), sets
    /// <c>manager.BoardConfidence01 = clamp(0.35 + delta, 0.05, 0.95)</c> and increments
    /// <c>manager.SeasonsAtClub</c>. Managers are never sacked here — that is <see cref="Process"/>.
    /// </summary>
    /// <param name="world">The world whose managers are reviewed.</param>
    /// <param name="outcome">The completed season's tables, positions and tiers.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> or <paramref name="outcome"/> is null.</exception>
    public static void UpdateBoardConfidence(WorldState world, SeasonOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(outcome);

        Dictionary<ClubId, int> expectedPositions = BuildExpectedPositions(world, outcome);

        foreach (WorldClub club in world.Clubs.All)
        {
            if (!TryGetManager(world, club, out WorldManager? manager) || manager is null) continue;

            LeagueTableRow row = outcome.RowFor(club.Id);
            int actualPosition = outcome.PositionOf(club.Id);
            int tier = outcome.TierOf(club.Id);

            float pointsPerGame = row.Played > 0 ? row.Points / (float)row.Played : 0f;
            float expectedPointsPerGame = ExpectedPointsPerGame(tier);
            int expectedPosition = expectedPositions[club.Id];

            float delta =
                PointsWeight * ((pointsPerGame - expectedPointsPerGame) / PointsPerGameScale) +
                PositionWeight * ((expectedPosition - actualPosition) / PositionScale);

            manager.BoardConfidence01 = Math.Clamp(
                NeutralBoardConfidence + delta, MinimumBoardConfidence, MaximumBoardConfidence);
            manager.SeasonsAtClub++;
        }
    }

    /// <summary>
    /// Runs the end-of-season carousel after <see cref="UpdateBoardConfidence"/>: for every club, in club-id
    /// order, the manager is sacked when his board confidence is below 0.30, or when the club finished six or
    /// more places below expectation and below 14th, or when a 0.04 roll hits for a club three or more places
    /// below expectation. A dismissed manager becomes an unemployed registry entry (his confidence and record
    /// stay as history) and a freshly generated manager replaces him.
    /// </summary>
    /// <param name="world">The world whose managers turn over.</param>
    /// <param name="outcome">The completed season's tables, positions and tiers.</param>
    /// <param name="rng">The managers random stream (the ecosystem passes the world's managers stream).</param>
    /// <returns>The sacking/hiring ledger.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public static ManagerCarouselResult Process(WorldState world, SeasonOutcome outcome, WorldRng rng)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(rng);

        Dictionary<ClubId, int> expectedPositions = BuildExpectedPositions(world, outcome);
        var changes = new List<ManagerChangeRecord>();

        foreach (WorldClub club in world.Clubs.All)
        {
            if (!TryGetManager(world, club, out WorldManager? outgoing) || outgoing is null) continue;

            int actualPosition = outcome.PositionOf(club.Id);
            int expectedPosition = expectedPositions[club.Id];
            int positionGap = expectedPosition - actualPosition;

            // Short-circuit order is deliberate: the random roll is only drawn for a club that was not already
            // dismissed by the deterministic rules, which keeps the rng consumption state-dependent-but-stable.
            bool sacked =
                outgoing.BoardConfidence01 < SackConfidenceThreshold ||
                (actualPosition >= expectedPosition + CatastrophicPositionGap &&
                    actualPosition > LastRespectablePosition) ||
                (positionGap <= -RandomSackUnderperformanceGap && rng.NextFloat() < RandomSackChance);

            if (!sacked) continue;

            WorldManager incoming = CreateReplacementManager(world, club, outgoing, rng);
            outgoing.ClubId = ClubId.None;
            world.Managers.Add(incoming);
            club.ManagerId = incoming.Id;

            changes.Add(new ManagerChangeRecord(
                club.Id, outgoing.Id, incoming.Id, WasSacked: true));
        }

        int unemployed = 0;
        foreach (WorldManager manager in world.Managers.All)
        {
            if (!manager.ClubId.IsValid) unemployed++;
        }

        return new ManagerCarouselResult(changes.Count, changes.Count, unemployed, changes.AsReadOnly());
    }

    // ---------------------------------------------------------------------------------------------
    // Expectation helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Expected final position (1..18) for every club in <paramref name="outcome"/>: within each division, the
    /// clubs are sorted by reputation (descending, ties by club id) and ranked 1..18. The season's division
    /// composition is used rather than the world's current one, so the review is correct whether or not
    /// promotion/relegation has already been applied.
    /// </summary>
    private static Dictionary<ClubId, int> BuildExpectedPositions(WorldState world, SeasonOutcome outcome)
    {
        var expectedPositions = new Dictionary<ClubId, int>(world.Clubs.Count);

        for (int tierIndex = 0; tierIndex < outcome.TablesByTier.Count; tierIndex++)
        {
            IReadOnlyList<LeagueTableRow> table = outcome.TablesByTier[tierIndex];
            var clubs = new List<WorldClub>(table.Count);
            for (int rowIndex = 0; rowIndex < table.Count; rowIndex++)
            {
                clubs.Add(world.Clubs[table[rowIndex].ClubId]);
            }

            clubs.Sort(static (left, right) =>
            {
                int byReputation = right.Reputation.CompareTo(left.Reputation);
                return byReputation != 0 ? byReputation : left.Id.Value.CompareTo(right.Id.Value);
            });

            for (int position = 0; position < clubs.Count; position++)
            {
                expectedPositions[clubs[position].Id] = position + 1;
            }
        }

        return expectedPositions;
    }

    // ---------------------------------------------------------------------------------------------
    // Replacement managers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds and registers a replacement manager: a name from the pack (different from the outgoing
    /// manager's), age 35..65, the tier's reputation band, one of the six tactical presets (with the plan's
    /// formation mapping), fresh board confidence and a zeroed match record. The new manager is not added to
    /// the registry here — the caller does that so the change and the registry cannot disagree.
    /// </summary>
    private static WorldManager CreateReplacementManager(
        WorldState world, WorldClub club, WorldManager outgoing, WorldRng rng)
    {
        NamePools names = world.Pack.Names;
        string firstName;
        string lastName;
        int attempts = 0;
        do
        {
            firstName = names.FirstNames[rng.NextIndex(names.FirstNames.Count)];
            lastName = names.LastNames[rng.NextIndex(names.LastNames.Count)];
            attempts++;
        }
        while (attempts < MaximumNameAttempts && IsSamePerson(firstName, lastName, outgoing));

        if (IsSamePerson(firstName, lastName, outgoing))
        {
            // Degenerate name pool: a suffix keeps the identity visibly distinct without a second draw loop.
            lastName = $"{lastName} II";
        }

        int age = MinimumManagerAge + rng.NextInt(0, MaximumManagerAge - MinimumManagerAge + 1);
        int reputationMinimum = ManagerReputationMinimumByTier[club.Tier];
        int reputation = rng.NextInt(reputationMinimum, ManagerReputationMaximumByTier[club.Tier] + 1);

        int preset = rng.NextIndex(TacticalPresetCount);
        TeamTacticalProfile tacticalProfile = CreateTacticalProfile(preset);
        FormationKind formation = FormationForPreset(preset);
        ManagerId id = world.AllocateManagerId();

        return new WorldManager(
            id, firstName, lastName, age, club.Id, tacticalProfile, formation, reputation)
        {
            BoardConfidence01 = NewManagerBoardConfidence,
            SeasonsAtClub = 0,
            MatchesInCharge = 0,
            Wins = 0,
            Draws = 0,
            Losses = 0
        };
    }

    /// <summary>True when a drawn name is the outgoing manager's name (case-insensitive).</summary>
    private static bool IsSamePerson(string firstName, string lastName, WorldManager manager) =>
        string.Equals(manager.FirstName, firstName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(manager.LastName, lastName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Maps one of the six preset indices onto the match engine's tactical-profile vocabulary.</summary>
    private static TeamTacticalProfile CreateTacticalProfile(int preset) => preset switch
    {
        PresetPossessionControl => TeamTacticalProfile.PossessionControl,
        1 => TeamTacticalProfile.Direct,
        2 => TeamTacticalProfile.DirectCounterattack,
        PresetHighPress => TeamTacticalProfile.HighPressGegenpress,
        4 => TeamTacticalProfile.LowBlock,
        5 => TeamTacticalProfile.ManOriented,
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown tactical preset.")
    };

    /// <summary>Plan §5.1: possession and pressing sides line up 4-3-3, everything else 4-4-2.</summary>
    private static FormationKind FormationForPreset(int preset) =>
        preset is PresetPossessionControl or PresetHighPress
            ? FormationKind.FourThreeThree
            : FormationKind.FourFourTwo;

    // ---------------------------------------------------------------------------------------------
    // Small lookups.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Resolves a club's current manager, tolerating a club that has no manager assigned.</summary>
    private static bool TryGetManager(WorldState world, WorldClub club, out WorldManager? manager)
    {
        if (!club.ManagerId.IsValid)
        {
            manager = null;
            return false;
        }

        return world.Managers.TryGet(club.ManagerId, out manager) && manager is not null;
    }
}
