using System;
using System.Collections.Generic;
using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>One club's chosen team for one match.</summary>
/// <param name="ClubId">Club the lineup belongs to.</param>
/// <param name="Formation">Formation the manager lined the side up in.</param>
/// <param name="StartingXI">Exactly eleven player ids; index 0 is the goalkeeper slot.</param>
/// <param name="StartingRoles">Exactly eleven slot roles (<see cref="Formations.StartingRoles"/> order).</param>
/// <param name="Bench">Exactly seven substitutes, in <see cref="Formations.BenchRoles"/> order.</param>
/// <param name="BenchRoles">Exactly seven bench slot roles.</param>
public sealed record SelectedLineup(
    ClubId ClubId,
    FormationKind Formation,
    IReadOnlyList<PlayerId> StartingXI,
    IReadOnlyList<WorldRole> StartingRoles,
    IReadOnlyList<PlayerId> Bench,
    IReadOnlyList<WorldRole> BenchRoles);

/// <summary>The weighted selection score, exposed for transparent squad-status UI/tests.</summary>
/// <param name="RoleSuitability01">Role suitability, 0..1 (plan §5.4).</param>
/// <param name="Form01">Recent-form rating, 0..1 (0.5 with no appearances).</param>
/// <param name="Condition01">Freshness, 0..1.</param>
/// <param name="Sharpness01">Match sharpness, 0..1.</param>
/// <param name="ManagerTrust01">Manager trust, 0..1.</param>
/// <param name="Total">The weighted total the selector ranks on.</param>
public readonly record struct SelectionScoreBreakdown(
    float RoleSuitability01,
    float Form01,
    float Condition01,
    float Sharpness01,
    float ManagerTrust01,
    float Total);

/// <summary>
/// The manager's squad selector (plan §5.4): scores every fit, unsuspended squad member for each slot and
/// fills the starting XI and the seven-man bench greedily, one slot at a time.
///
/// <para>Selection score: <c>0.50 * RoleSuitability01 + 0.18 * RecentForm01 + 0.14 * Condition01 +
/// 0.10 * MatchSharpness01 + 0.08 * ManagerTrust01</c>. The goalkeeper slot prefers a natural keeper
/// (suitability at least 50) and only falls back to any remaining player when none is available, so a
/// squad with no fit keeper is still fielded.</para>
///
/// <para>Every tie is broken by the lower <see cref="PlayerId"/> value and every candidate is visited in
/// the club's registration order, so two selections over the same world produce the identical team.</para>
/// </summary>
public static class SquadSelector
{
    /// <summary>Players required to field a starting XI.</summary>
    public const int SquadSizeRequired = 11;

    /// <summary>Lowest goalkeeper suitability that counts as a natural keeper for the keeper slot.</summary>
    private const float GoalkeeperSuitabilityFloor = 50f;

    /// <summary>Appearances averaged for recent form.</summary>
    private const int FormWindow = 8;

    /// <summary>Formula weights, in the plan §5.4 order.</summary>
    private const float RoleSuitabilityWeight = 0.50f;
    private const float FormWeight = 0.18f;
    private const float ConditionWeight = 0.14f;
    private const float SharpnessWeight = 0.10f;
    private const float ManagerTrustWeight = 0.08f;

    /// <summary>Rank below which an available player is a key starter when the club cannot field a full XI.</summary>
    private const int KeyStarterRank = 11;

    private const string KeyStarterStatus = "Key Starter";
    private const string FirstTeamStatus = "First Team";
    private const string RotationStatus = "Rotation / Bench";
    private const string ReservesStatus = "Reserves";

    /// <summary>
    /// Picks the strongest available XI in the manager's formation plus a seven-player bench.
    /// </summary>
    /// <param name="world">The world whose player registry resolves the club's squad.</param>
    /// <param name="club">The club to select for.</param>
    /// <returns>The selected XI and bench, in slot order.</returns>
    /// <exception cref="InvalidOperationException">
    /// Fewer than <see cref="SquadSizeRequired"/> players are available, or fewer than
    /// <see cref="SquadSizeRequired"/> + <see cref="Formations.BenchSize"/> are available (an XI and a full
    /// bench are always returned).
    /// </exception>
    public static SelectedLineup Select(WorldState world, WorldClub club)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(club);

        FormationKind formation = ResolveFormation(world, club);
        IReadOnlyList<WorldRole> startingRoles = Formations.StartingRoles(formation);
        IReadOnlyList<WorldRole> benchRoles = Formations.BenchRoles();

        int squadCount = club.Squad.Count;
        var players = new WorldPlayer[squadCount];
        var available = new bool[squadCount];
        int availableCount = 0;
        for (int i = 0; i < squadCount; i++)
        {
            WorldPlayer player = world.Players[club.Squad[i]];
            players[i] = player;
            bool canPlay = IsAvailable(player);
            available[i] = canPlay;
            if (canPlay) availableCount++;
        }

        if (availableCount < SquadSizeRequired)
        {
            throw new InvalidOperationException(
                $"Club '{club.Name}' has {availableCount} available players; a starting XI needs " +
                $"{SquadSizeRequired}.");
        }

        int matchdaySquadSize = SquadSizeRequired + Formations.BenchSize;
        if (availableCount < matchdaySquadSize)
        {
            throw new InvalidOperationException(
                $"Club '{club.Name}' has {availableCount} available players; an XI plus a " +
                $"{Formations.BenchSize}-player bench needs {matchdaySquadSize}.");
        }

        var selected = new bool[squadCount];
        var startingXI = new PlayerId[SquadSizeRequired];
        var resolvedStartingRoles = new WorldRole[SquadSizeRequired];
        for (int slot = 0; slot < SquadSizeRequired; slot++)
        {
            WorldRole role = startingRoles[slot];
            int pick = FindBest(
                players, available, selected, club, role,
                preferNaturalGoalkeeper: role == WorldRole.Goalkeeper);
            if (pick < 0 && role == WorldRole.Goalkeeper)
            {
                // No natural keeper is available: the plan allows any remaining player to go in goal.
                pick = FindBest(players, available, selected, club, role, preferNaturalGoalkeeper: false);
            }

            if (pick < 0)
            {
                throw new InvalidOperationException(
                    $"Club '{club.Name}' ran out of available players while filling the starting XI.");
            }

            selected[pick] = true;
            startingXI[slot] = players[pick].Id;
            resolvedStartingRoles[slot] = role;
        }

        var bench = new PlayerId[Formations.BenchSize];
        for (int slot = 0; slot < bench.Length; slot++)
        {
            int pick = FindBest(players, available, selected, club, benchRoles[slot], preferNaturalGoalkeeper: false);
            if (pick < 0)
            {
                throw new InvalidOperationException(
                    $"Club '{club.Name}' ran out of available players while filling the bench.");
            }

            selected[pick] = true;
            bench[slot] = players[pick].Id;
        }

        return new SelectedLineup(club.Id, formation, startingXI, resolvedStartingRoles, bench, benchRoles);
    }

    /// <summary>The weighted selection score of one player for one slot role, 0..1.</summary>
    /// <param name="player">The player being scored.</param>
    /// <param name="role">The slot role to score him for.</param>
    /// <param name="club">His club (supplies manager trust).</param>
    public static float SelectionScore(WorldPlayer player, WorldRole role, WorldClub club) =>
        ScoreBreakdown(player, role, club).Total;

    /// <summary>
    /// The full selection score breakdown, so a UI or a test can show exactly why a player was picked.
    /// </summary>
    /// <param name="player">The player being scored.</param>
    /// <param name="role">The slot role to score him for.</param>
    /// <param name="club">His club (supplies manager trust).</param>
    public static SelectionScoreBreakdown ScoreBreakdown(WorldPlayer player, WorldRole role, WorldClub club)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(club);

        float roleSuitability01 = Math.Clamp(IdentityEvaluator.Suitability(player, role) / 100f, 0f, 1f);
        float form01 = player.Form.Form01(FormWindow);
        float condition01 = player.Condition01;
        float sharpness01 = player.MatchSharpness01;
        float managerTrust01 = club.ManagerTrustOf(player.Id);

        float total =
            RoleSuitabilityWeight * roleSuitability01 +
            FormWeight * form01 +
            ConditionWeight * condition01 +
            SharpnessWeight * sharpness01 +
            ManagerTrustWeight * managerTrust01;

        return new SelectionScoreBreakdown(
            roleSuitability01, form01, condition01, sharpness01, managerTrust01, total);
    }

    /// <summary>True when the player can play at all (not retired, not injured, not suspended).</summary>
    public static bool IsAvailable(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player.IsAvailableForSelection;
    }

    /// <summary>
    /// The player's squad status, derived from the actual matchday selection: the starting XI are key
    /// starters, the bench is first-team cover, other available squad members are rotation/bench cover,
    /// and unavailable players (injured, suspended or retired) are reserves. When the club cannot field a
    /// full XI plus bench, the status falls back to the raw selection ranking (the first eleven available
    /// players are key starters). Players outside the club's squad are reserves.
    /// </summary>
    /// <param name="world">The world whose player registry resolves the club's squad.</param>
    /// <param name="club">The club to report on.</param>
    /// <param name="playerId">The player to report.</param>
    public static string SquadStatusOf(WorldState world, WorldClub club, PlayerId playerId)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(club);
        if (!playerId.IsValid) throw new ArgumentOutOfRangeException(nameof(playerId), playerId, "A valid player id is required.");

        bool inSquad = false;
        foreach (PlayerId id in club.Squad)
        {
            if (id != playerId) continue;
            inSquad = true;
            break;
        }

        if (!inSquad) return ReservesStatus;

        WorldPlayer player = world.Players[playerId];
        if (!IsAvailable(player)) return ReservesStatus;

        FormationKind formation = ResolveFormation(world, club);
        int availableCount = CountAvailable(world, club);
        int matchdaySquadSize = SquadSizeRequired + Formations.BenchSize;
        if (availableCount >= matchdaySquadSize)
        {
            SelectedLineup lineup = Select(world, club);
            if (ContainsPlayer(lineup.StartingXI, playerId)) return KeyStarterStatus;
            if (ContainsPlayer(lineup.Bench, playerId)) return FirstTeamStatus;
            return RotationStatus;
        }

        // Degenerate squad: there is no full matchday selection, so rate every available player by his best
        // slot score and keep the top eleven as key starters.
        IReadOnlyList<WorldRole> roles = Formations.StartingRoles(formation);
        var ranked = new List<(PlayerId Id, float Score)>(availableCount);
        foreach (PlayerId id in club.Squad)
        {
            WorldPlayer candidate = world.Players[id];
            if (!IsAvailable(candidate)) continue;

            float best = float.NegativeInfinity;
            for (int slot = 0; slot < roles.Count; slot++)
            {
                float score = SelectionScore(candidate, roles[slot], club);
                if (score > best) best = score;
            }

            ranked.Add((id, best));
        }

        ranked.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Id.Value.CompareTo(right.Id.Value);
        });

        for (int rank = 0; rank < ranked.Count; rank++)
        {
            if (ranked[rank].Id == playerId)
            {
                return rank < KeyStarterRank ? KeyStarterStatus : RotationStatus;
            }
        }

        return RotationStatus;
    }

    // ---------------------------------------------------------------------------------------------
    // Internals.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The manager's formation, or the balanced 4-4-2 default when the club has no manager.</summary>
    private static FormationKind ResolveFormation(WorldState world, WorldClub club)
    {
        if (club.ManagerId.IsValid &&
            world.Managers.TryGet(club.ManagerId, out WorldManager? manager) &&
            manager is not null)
        {
            return manager.Formation;
        }

        return FormationKind.FourFourTwo;
    }

    /// <summary>Number of squad members who can play in this match.</summary>
    private static int CountAvailable(WorldState world, WorldClub club)
    {
        int count = 0;
        foreach (PlayerId id in club.Squad)
        {
            if (IsAvailable(world.Players[id])) count++;
        }

        return count;
    }

    /// <summary>True when the ordered id list contains the player (no allocation, unlike LINQ Contains).</summary>
    private static bool ContainsPlayer(IReadOnlyList<PlayerId> playerIds, PlayerId playerId)
    {
        for (int i = 0; i < playerIds.Count; i++)
        {
            if (playerIds[i] == playerId) return true;
        }

        return false;
    }

    /// <summary>
    /// Highest-scoring available, not-yet-selected player for one slot role. Ties break by the lower
    /// <see cref="PlayerId"/> value, which makes the result independent of hash or iteration order.
    /// </summary>
    private static int FindBest(
        WorldPlayer[] players,
        bool[] available,
        bool[] selected,
        WorldClub club,
        WorldRole role,
        bool preferNaturalGoalkeeper)
    {
        int best = -1;
        float bestScore = float.NegativeInfinity;
        PlayerId bestId = default;
        for (int i = 0; i < players.Length; i++)
        {
            if (!available[i] || selected[i]) continue;

            WorldPlayer player = players[i];
            if (preferNaturalGoalkeeper &&
                IdentityEvaluator.Suitability(player, WorldRole.Goalkeeper) < GoalkeeperSuitabilityFloor)
            {
                continue;
            }

            float score = SelectionScore(player, role, club);
            if (best < 0 || score > bestScore || (score == bestScore && player.Id.Value < bestId.Value))
            {
                best = i;
                bestScore = score;
                bestId = player.Id;
            }
        }

        return best;
    }
}
