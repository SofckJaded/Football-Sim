using FootballSim.World.Core;
using FootballSim.World.Players;
using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>
/// Broad squad area of a player. This is a roster-building classification, not a position lock: the
/// emergent identity (<see cref="IdentityEvaluator"/>) still decides what the player actually is on the
/// pitch. Squad depth, youth intake and the transfer market only need to know which area a player fills.
/// </summary>
public enum RosterGroup
{
    /// <summary>Goalkeepers.</summary>
    Goalkeeper = 0,

    /// <summary>Centre-backs, full-backs and defensive midfielders are counted separately by suitability,
    /// but for depth purposes all defenders are one group.</summary>
    Defender = 1,

    /// <summary>Central and attacking midfielders.</summary>
    Midfielder = 2,

    /// <summary>Wingers and strikers.</summary>
    Forward = 3
}

/// <summary>
/// Classifies players into broad squad areas and reports a club's depth by area. Used by the transfer
/// market (which area needs strengthening), youth intake (which area to replenish) and squad reports.
///
/// The classification is attribute-based because the world deliberately does not store a fixed position:
/// a player whose goalkeeping attribute clearly dominates his own profile is a keeper, otherwise the
/// strongest of three transparent composites (defensive, midfield, attacking) wins.
/// </summary>
public static class RosterAnalyser
{
    /// <summary>Minimum credible depth per group for a 22–25 player squad.</summary>
    public static IReadOnlyDictionary<RosterGroup, int> MinimumDepth { get; } =
        new Dictionary<RosterGroup, int>
        {
            [RosterGroup.Goalkeeper] = 2,
            [RosterGroup.Defender] = 7,
            [RosterGroup.Midfielder] = 7,
            [RosterGroup.Forward] = 4
        };

    /// <summary>Classifies one player into a broad squad area.</summary>
    public static RosterGroup GroupOf(WorldPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);

        PlayerAttributeSet attributes = player.Attributes;
        float ownMean = attributes.MeanCurrent();
        float goalkeeping = attributes.Current(AttributeId.Goalkeeping);

        // A keeper's goalkeeping ability stands clearly above his own overall level; an outfielder's does
        // not (the generator rolls outfielders' goalkeeping aptitude far below their other aptitudes).
        if (goalkeeping >= 42f && goalkeeping >= ownMean + 5f) return RosterGroup.Goalkeeper;

        float defence =
            (attributes.Current(AttributeId.Tackling) +
             attributes.Current(AttributeId.Positioning) +
             attributes.Current(AttributeId.Strength) +
             attributes.Current(AttributeId.Balance) +
             attributes.Current(AttributeId.Composure)) * 0.20f;

        float midfield =
            (attributes.Current(AttributeId.Passing) +
             attributes.Current(AttributeId.Vision) +
             attributes.Current(AttributeId.Stamina) +
             attributes.Current(AttributeId.FirstTouch) +
             attributes.Current(AttributeId.Composure)) * 0.20f;

        float attack =
            (attributes.Current(AttributeId.Finishing) +
             attributes.Current(AttributeId.OffTheBall) +
             attributes.Current(AttributeId.TopSpeed) +
             attributes.Current(AttributeId.Dribbling) +
             attributes.Current(AttributeId.ShotPower)) * 0.20f;

        if (defence >= midfield && defence >= attack) return RosterGroup.Defender;
        return midfield >= attack ? RosterGroup.Midfielder : RosterGroup.Forward;
    }

    /// <summary>Number of players in one area.</summary>
    public static int Count(WorldState world, WorldClub club, RosterGroup group)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(club);

        int count = 0;
        foreach (PlayerId playerId in club.Squad)
        {
            if (!world.Players.TryGet(playerId, out WorldPlayer? player) || player is null) continue;
            if (GroupOf(player) == group) count++;
        }
        return count;
    }

    /// <summary>Depth census for a club, in <see cref="RosterGroup"/> order.</summary>
    public static IReadOnlyDictionary<RosterGroup, int> Census(WorldState world, WorldClub club)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(club);

        var census = new Dictionary<RosterGroup, int>(4)
        {
            [RosterGroup.Goalkeeper] = 0,
            [RosterGroup.Defender] = 0,
            [RosterGroup.Midfielder] = 0,
            [RosterGroup.Forward] = 0
        };

        foreach (PlayerId playerId in club.Squad)
        {
            if (!world.Players.TryGet(playerId, out WorldPlayer? player) || player is null) continue;
            census[GroupOf(player)]++;
        }
        return census;
    }

    /// <summary>
    /// The area that most needs a body (largest shortfall below <see cref="MinimumDepth"/>), or null when
    /// every area is at or above its minimum. Ties break in <see cref="RosterGroup"/> order.
    /// </summary>
    public static RosterGroup? FindShortfall(WorldState world, WorldClub club)
    {
        IReadOnlyDictionary<RosterGroup, int> census = Census(world, club);
        RosterGroup? worst = null;
        int worstShortfall = 0;

        foreach (RosterGroup group in new[]
                 { RosterGroup.Goalkeeper, RosterGroup.Defender, RosterGroup.Midfielder, RosterGroup.Forward })
        {
            int shortfall = MinimumDepth[group] - census[group];
            if (shortfall > worstShortfall)
            {
                worstShortfall = shortfall;
                worst = group;
            }
        }
        return worst;
    }

    /// <summary>
    /// A player's suitability for one broad area, 0..100. Used to rank transfer targets and choose which
    /// prospect a youth intake should be.
    /// </summary>
    public static float AreaScore(WorldPlayer player, RosterGroup group)
    {
        ArgumentNullException.ThrowIfNull(player);
        PlayerAttributeSet attributes = player.Attributes;

        return group switch
        {
            RosterGroup.Goalkeeper =>
                attributes.Current(AttributeId.Goalkeeping) * 0.75f +
                attributes.Current(AttributeId.Composure) * 0.25f,
            RosterGroup.Defender =>
                (attributes.Current(AttributeId.Tackling) +
                 attributes.Current(AttributeId.Positioning) +
                 attributes.Current(AttributeId.Strength) +
                 attributes.Current(AttributeId.Balance)) * 0.25f,
            RosterGroup.Midfielder =>
                (attributes.Current(AttributeId.Passing) +
                 attributes.Current(AttributeId.Vision) +
                 attributes.Current(AttributeId.Stamina) +
                 attributes.Current(AttributeId.FirstTouch)) * 0.25f,
            _ =>
                (attributes.Current(AttributeId.Finishing) +
                 attributes.Current(AttributeId.OffTheBall) +
                 attributes.Current(AttributeId.TopSpeed) +
                 attributes.Current(AttributeId.Dribbling)) * 0.25f
        };
    }
}
