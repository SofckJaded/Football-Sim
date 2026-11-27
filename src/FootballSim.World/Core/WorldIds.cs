using System;

namespace FootballSim.World.Core;

/// <summary>
/// Stable identity of a player inside one world. World ids are one-based, assigned once during world
/// generation and never reused within a world, so a saved id always resolves to the same entity (or
/// reports "missing") and a retired player's number is not recycled onto a new player.
/// </summary>
/// <param name="Value">One-based numeric identity; 0 is the <see cref="None"/> sentinel.</param>
public readonly record struct PlayerId(int Value) : IComparable<PlayerId>
{
    /// <summary>The unset sentinel. It is never a valid player id.</summary>
    public static PlayerId None => default;

    /// <summary>Whether this id refers to a real player (value greater than 0).</summary>
    public bool IsValid => Value > 0;

    /// <summary>Orders ids by their numeric value.</summary>
    public int CompareTo(PlayerId other) => Value.CompareTo(other.Value);

    /// <summary>Compact identity form used in logs and save files, e.g. <c>P17</c>.</summary>
    public override string ToString() => $"P{Value}";
}

/// <summary>
/// Stable identity of a club inside one world. World ids are one-based, assigned once during world
/// generation and never reused within a world, so a saved id keeps resolving to the same club.
/// </summary>
/// <param name="Value">One-based numeric identity; 0 is the <see cref="None"/> sentinel.</param>
public readonly record struct ClubId(int Value) : IComparable<ClubId>
{
    /// <summary>The unset sentinel. It is never a valid club id.</summary>
    public static ClubId None => default;

    /// <summary>Whether this id refers to a real club (value greater than 0).</summary>
    public bool IsValid => Value > 0;

    /// <summary>Orders ids by their numeric value.</summary>
    public int CompareTo(ClubId other) => Value.CompareTo(other.Value);

    /// <summary>Compact identity form used in logs and save files, e.g. <c>C42</c>.</summary>
    public override string ToString() => $"C{Value}";
}

/// <summary>
/// Stable identity of a manager inside one world. World ids are one-based, assigned once during world
/// generation and never reused within a world, so a saved id keeps resolving to the same manager.
/// </summary>
/// <param name="Value">One-based numeric identity; 0 is the <see cref="None"/> sentinel.</param>
public readonly record struct ManagerId(int Value) : IComparable<ManagerId>
{
    /// <summary>The unset sentinel. It is never a valid manager id.</summary>
    public static ManagerId None => default;

    /// <summary>Whether this id refers to a real manager (value greater than 0).</summary>
    public bool IsValid => Value > 0;

    /// <summary>Orders ids by their numeric value.</summary>
    public int CompareTo(ManagerId other) => Value.CompareTo(other.Value);

    /// <summary>Compact identity form used in logs and save files, e.g. <c>M3</c>.</summary>
    public override string ToString() => $"M{Value}";
}

/// <summary>
/// Stable identity of a league (division) inside one world. World ids are one-based, assigned once during
/// world generation and never reused within a world, so a saved id keeps resolving to the same league.
/// </summary>
/// <param name="Value">One-based numeric identity; 0 is the <see cref="None"/> sentinel.</param>
public readonly record struct LeagueId(int Value) : IComparable<LeagueId>
{
    /// <summary>The unset sentinel. It is never a valid league id.</summary>
    public static LeagueId None => default;

    /// <summary>Whether this id refers to a real league (value greater than 0).</summary>
    public bool IsValid => Value > 0;

    /// <summary>Orders ids by their numeric value.</summary>
    public int CompareTo(LeagueId other) => Value.CompareTo(other.Value);

    /// <summary>Compact identity form used in logs and save files, e.g. <c>L2</c>.</summary>
    public override string ToString() => $"L{Value}";
}

/// <summary>
/// Stable identity of a match reference inside one world. World ids are one-based, assigned once during
/// world generation and never reused within a world, so a saved id keeps resolving to the same fixture.
/// A match id is used both for a fixture row and for the quick-sim result attached to it.
/// </summary>
/// <param name="Value">One-based numeric identity; 0 is the <see cref="None"/> sentinel.</param>
public readonly record struct MatchId(int Value) : IComparable<MatchId>
{
    /// <summary>The unset sentinel. It is never a valid match id.</summary>
    public static MatchId None => default;

    /// <summary>Whether this id refers to a real fixture (value greater than 0).</summary>
    public bool IsValid => Value > 0;

    /// <summary>Fixture alias of <see cref="IsValid"/>: a valid match id is a fixture/quick-sim reference.</summary>
    public bool IsFixture => IsValid;

    /// <summary>Orders ids by their numeric value.</summary>
    public int CompareTo(MatchId other) => Value.CompareTo(other.Value);

    /// <summary>Compact identity form used in logs and save files, e.g. <c>X2026</c>.</summary>
    public override string ToString() => $"X{Value}";
}
