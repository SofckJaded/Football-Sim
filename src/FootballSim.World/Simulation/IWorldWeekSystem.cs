using FootballSim.World.World;

namespace FootballSim.World.Simulation;

/// <summary>
/// A per-week world extension: the hook the season runner calls around each week so ecosystem systems
/// (scouting, finances, transfer windows — W3) run inside the season loop instead of being bolted on
/// afterwards.
///
/// The runner is the only authority on when a week starts and ends, so an extension never has to guess
/// the calendar. Both hooks are called exactly once per <see cref="WorldSeasonRunner.AdvanceWeek"/>;
/// implementations must be deterministic and must mutate only the world they are given.
/// </summary>
public interface IWorldWeekSystem
{
    /// <summary>Called before any player tick or fixture in the week is processed.</summary>
    void OnWeekStart(WorldState world);

    /// <summary>
    /// Called after every fixture, growth tick and condition update of the week, before the calendar
    /// advances. The report describes the fixtures that were played.
    /// </summary>
    void OnWeekEnd(WorldState world, WeekReport report);
}
