using System;
using System.Collections.Generic;
using System.Linq;

namespace FootballSim;

/// <summary>
/// One selectable pre-match position. <see cref="PlayerId"/> is the home team player id
/// (shirt number) that the human controller locks onto, or <c>null</c> for a full AI match.
/// </summary>
public sealed record MatchPositionOption(string Label, string ShirtNumber, int? PlayerId)
{
    public string DisplayLabel => PlayerId.HasValue ? $"{Label} ({ShirtNumber})" : Label;
}

/// <summary>
/// Positions offered on the Match setup page. Shirt numbers follow
/// <c>TeamTacticalCoordinator.GetFormationRoleAndSlot</c> for an 11-a-side side.
/// </summary>
public static class MatchPositions
{
    public static readonly IReadOnlyList<MatchPositionOption> All = new List<MatchPositionOption>
    {
        new("None — full AI simulation", "—", null),
        new("Goalkeeper", "#1", 1),
        new("Right Back", "#2", 2),
        new("Left Back", "#3", 3),
        new("Centre Back", "#4", 4),
        new("Centre Back", "#5", 5),
        new("Defensive Midfielder", "#6", 6),
        new("Right Wing", "#7", 7),
        new("Central Midfielder", "#8", 8),
        new("Striker", "#9", 9),
        new("Attacking Midfielder", "#10", 10),
        new("Left Wing", "#11", 11)
    };

    /// <summary>The pre-match lock used before the menu existed (home striker, #9).</summary>
    public const int DefaultPlayerId = 9;

    public static MatchPositionOption Default =>
        All.FirstOrDefault(p => p.PlayerId == DefaultPlayerId) ?? All[0];

    public static MatchPositionOption Find(int? playerId)
    {
        if (playerId is null) return All[0];
        return All.FirstOrDefault(p => p.PlayerId == playerId.Value) ?? All[0];
    }

    public static int IndexOf(int? playerId)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i].PlayerId == playerId) return i;
        }
        return 0;
    }

    /// <summary>
    /// The player id the match inspector should show for a match with <paramref name="playerCount"/>
    /// players (audit F22).
    ///
    /// The HUD used to index <c>Players[playerId - 1]</c>, which assumes the designated human id is a
    /// valid one-based array index. It is not a general contract: a 5-a-side match has ten players, so
    /// the default striker (#9) does not exist and the lookup reads a different player; a spectator
    /// match has no human id at all. Never index the squad by id.
    /// </summary>
    public static int ResolveInspectedPlayerId(int? humanPlayerId, int playerCount)
    {
        if (playerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(playerCount), playerCount,
                "A match must have at least one player to inspect.");

        int candidate = humanPlayerId ?? DefaultPlayerId;
        if (candidate >= 1 && candidate <= playerCount) return candidate;

        // Fall back to the default striker, clamped into the squad that actually exists.
        return Math.Clamp(DefaultPlayerId, 1, playerCount);
    }
}

/// <summary>
/// Half-length choices for the Match setup page, in minutes. Four minutes is the default
/// (an 8 minute match), which keeps a full 11v11 simulation watchable in one sitting.
/// </summary>
public static class MatchPresets
{
    public const int DefaultHalfLengthMinutes = 4;

    public static readonly IReadOnlyList<int> HalfLengthMinuteOptions = new List<int>
    {
        1, 2, 3, 4, 5, 6, 8, 10, 15, 20, 30, 45
    };

    public static int ClampHalfLengthMinutes(int minutes)
    {
        int best = HalfLengthMinuteOptions[0];
        int bestDistance = int.MaxValue;
        foreach (int option in HalfLengthMinuteOptions)
        {
            int distance = Math.Abs(option - minutes);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = option;
            }
        }
        return best;
    }

    public static string DescribeHalfLength(int minutes) =>
        minutes == DefaultHalfLengthMinutes ? $"{minutes} min (default)" : $"{minutes} min";
}

/// <summary>
/// Snapshot of the Match setup page handed from the menu to the match scene.
/// </summary>
public sealed class MatchSetupOptions
{
    public const float DefaultHalfDurationSeconds = MatchPresets.DefaultHalfLengthMinutes * 60f;

    public int? HumanPlayerId { get; init; } = MatchPositions.DefaultPlayerId;
    public float HalfDurationSeconds { get; init; } = DefaultHalfDurationSeconds;

    public bool IsSpectatorMatch => HumanPlayerId is null;

    public MatchPositionOption Position => MatchPositions.Find(HumanPlayerId);

    public int HalfLengthMinutes => MatchPresets.ClampHalfLengthMinutes(
        (int)MathF.Round(HalfDurationSeconds / 60f));
}
