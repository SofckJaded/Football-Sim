using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;
using FootballSim.Locomotion;

namespace FootballSim.AI;

/// <summary>
/// One team's picture of its opponents (A2.6): the freshest thing any of its players has actually seen,
/// never the authoritative positions.
///
/// This exists because the knowledge boundary was only enforced player-by-player until now. The
/// players' own decisions read <see cref="FootballerObservation"/>, but the team-level consumers — the
/// persistent job coordinator above all — read the live <see cref="FootballerState"/> list, so a team's
/// *structure* could still react instantly to an opponent nobody on that team had seen. That is exactly
/// what the A2 exit gate names ("an unseen opponent changing direction does not instantly change ... a
/// team assignment").
///
/// The view is built by unioning the team's observations and keeping the youngest sighting per opponent.
/// <see cref="FromTruth"/> builds the same shape from live state for validation and diagnostics only: the
/// two are deliberately the same type so a test can compare them and prove the belief lags where it
/// should.
/// </summary>
public sealed class TeamBelief
{
    /// <summary>One opponent as this team believes them to be.</summary>
    public readonly record struct Opponent(
        int PlayerId,
        TacticalRole Role,
        Vector2 PositionXZ,
        Vector2 VelocityXZ,
        int AgeTicks,
        float Confidence01)
    {
        /// <summary>True when this came from a sighting this tick.</summary>
        public bool IsFresh => AgeTicks == 0;
    }

    private readonly Dictionary<int, Opponent> _byId = new();
    private readonly List<Opponent> _opponents = new();
    private readonly List<int> _order = new();

    public TeamBelief(TeamId team) => Team = team;

    public TeamId Team { get; }

    /// <summary>
    /// True when the view was built from authoritative state. Diagnostics and tests only — a decision
    /// must never read a truth view.
    /// </summary>
    public bool IsTruthView { get; private set; }

    /// <summary>Opponents this team currently believes in, in ascending player id for determinism.</summary>
    public IReadOnlyList<Opponent> Opponents => _opponents;

    public int Count => _opponents.Count;

    /// <summary>True once the view has been built at least once.</summary>
    public bool HasBeenBuilt { get; private set; }

    /// <summary>
    /// Rebuilds from what this team's players can see. An opponent nobody on the team has ever seen is
    /// simply absent: a team does not get to know where a player is because he exists.
    /// </summary>
    public void RebuildFromObservations(
        IReadOnlyList<FootballerState> players,
        IReadOnlyDictionary<int, FootballerObservation> latestObservations)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(latestObservations);
        IsTruthView = false;

        _byId.Clear();
        foreach (FootballerState player in players)
        {
            if (player.Team != Team || !player.IsActiveOnPitch) continue;
            if (!latestObservations.TryGetValue(player.Id, out FootballerObservation? observation)) continue;

            foreach (PerceivedEntity entity in observation.VisiblePlayers)
            {
                if (entity.Team == Team) continue;

                var seen = new Opponent(
                    entity.PlayerId,
                    entity.Role,
                    new Vector2(entity.EstimatedPosition.X, entity.EstimatedPosition.Z),
                    new Vector2(entity.EstimatedVelocity.X, entity.EstimatedVelocity.Z),
                    entity.ObservedAgeTicks,
                    entity.Confidence01);

                if (_byId.TryGetValue(seen.PlayerId, out Opponent existing) &&
                    existing.AgeTicks <= seen.AgeTicks)
                {
                    continue;
                }

                _byId[seen.PlayerId] = seen;
            }
        }

        Publish();
    }

    /// <summary>
    /// Builds the view from live state. Validation and diagnostics only: this is the truth a team is not
    /// entitled to, and it is a distinct, labelled object so the difference stays visible.
    /// </summary>
    public static TeamBelief FromTruth(TeamId team, IReadOnlyList<FootballerState> players)
    {
        var belief = new TeamBelief(team) { IsTruthView = true };
        foreach (FootballerState player in players)
        {
            if (player.Team == team || !player.IsActiveOnPitch) continue;

            belief._byId[player.Id] = new Opponent(
                player.Id,
                player.Role,
                new Vector2(player.Position.X, player.Position.Z),
                new Vector2(player.Velocity.X, player.Velocity.Z),
                AgeTicks: 0,
                Confidence01: 1f);
        }

        belief.Publish();
        return belief;
    }

    /// <summary>Replaces the published order with a deterministic one (ascending player id).</summary>
    private void Publish()
    {
        _opponents.Clear();
        _order.Clear();
        if (_byId.Count > 0)
        {
            // Ascending player id keeps the published order independent of hash order.
            foreach (int id in _byId.Keys) _order.Add(id);
            _order.Sort();
            for (int i = 0; i < _order.Count; i++) _opponents.Add(_byId[_order[i]]);
        }
        HasBeenBuilt = true;
    }

    /// <summary>
    /// Distance from a point to the nearest opponent this team believes in, capped at
    /// <paramref name="cap"/> so an empty belief does not report "nobody anywhere useful".
    /// </summary>
    public float NearestDistance(Vector2 pointXZ, float cap = 15f)
    {
        float nearest = cap;
        for (int i = 0; i < _opponents.Count; i++)
        {
            float distance = Vector2.Distance(pointXZ, _opponents[i].PositionXZ);
            if (distance < nearest) nearest = distance;
        }
        return nearest;
    }

    /// <summary>
    /// Projection of the second-last believed opponent along the attacking direction, which is what the
    /// offside line is drawn from. False when fewer than two opponents are known — the caller must then
    /// fall back to a conservative line rather than invent a defender.
    /// </summary>
    public bool TryGetSecondLastProjX(float attackDirX, out float projection)
    {
        if (_opponents.Count < 2)
        {
            projection = 0f;
            return false;
        }

        float best = float.MinValue;
        float second = float.MinValue;
        for (int i = 0; i < _opponents.Count; i++)
        {
            float proj = _opponents[i].PositionXZ.X * attackDirX;
            if (proj > best)
            {
                second = best;
                best = proj;
            }
            else if (proj > second)
            {
                second = proj;
            }
        }

        projection = second;
        return true;
    }

    /// <summary>Oldest age among the believed opponents, in ticks; 0 when nothing is known.</summary>
    public int OldestAgeTicks
    {
        get
        {
            int oldest = 0;
            for (int i = 0; i < _opponents.Count; i++)
                if (_opponents[i].AgeTicks > oldest) oldest = _opponents[i].AgeTicks;
            return oldest;
        }
    }
}
