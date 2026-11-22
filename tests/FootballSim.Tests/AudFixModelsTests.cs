using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regression tests for the model findings of docs/Bug_Audit_2026-11-22.md (AUD-04, AUD-06, AUD-09,
/// AUD-11).
///
/// These are unit tests on synthetic inputs, not match tests: each fixture builds exactly the noisy or
/// ambiguous situation the audit describes and asserts the model's answer, so a match-level regression
/// (which can pass for unrelated reasons) is not needed to prove the defect. Each test is written so it
/// <b>fails on the pre-fix code</b> and passes after; the comment above each assertion says why.
/// </summary>
public sealed class AudFixModelsTests
{
    // ---------------------------------------------------------------------------------------------
    // AUD-04 — the team belief preferred the goalkeeper's noisy equal-age sighting
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both the goalkeeper (seeded first by <c>InitializeSquads</c>) and a close defender see the same
    /// opponent on the same tick. Before the fix the belief kept whichever was scanned first — always the
    /// keeper — so the team's jobs, line and press read his long-range coordinates instead of the
    /// defender's near-exact ones. Equal age must now be decided by confidence.
    /// </summary>
    [Fact]
    public void AUD04_TeamBelief_KeepsTheConfidentEqualAgeSighting()
    {
        FootballerState keeper = Player(1, TeamId.Home, TacticalRole.Goalkeeper, new Vector3(-40f, 0f, 0f));
        FootballerState defender = Player(2, TeamId.Home, TacticalRole.CenterBack, new Vector3(-8f, 0f, 0f));
        FootballerState opponent = Player(11, TeamId.Away, TacticalRole.Striker, new Vector3(6f, 0f, 0f));
        var players = new[] { keeper, defender, opponent };

        // Both sightings are from this tick (ObservedAgeTicks == 0). The keeper's is the longest range and
        // sits at the confidence floor; the defender's is a near-exact close look.
        PerceivedEntity keeperSighting = Sighting(11, new Vector3(10f, 0f, 4f), confidence01: 0.20f, ageTicks: 0);
        PerceivedEntity defenderSighting = Sighting(11, new Vector3(6.2f, 0f, 0.1f), confidence01: 0.95f, ageTicks: 0);

        var belief = new TeamBelief(TeamId.Home);
        belief.RebuildFromObservations(players, new Dictionary<int, FootballerObservation>
        {
            [keeper.Id] = Observation(keeper, keeperSighting),
            [defender.Id] = Observation(defender, defenderSighting)
        });

        Assert.Equal(1, belief.Count);
        TeamBelief.Opponent seen = belief.Opponents[0];
        // Pre-fix: AgeTicks 0 <= 0 skipped the defender's sighting, leaving confidence 0.20 at (10, 4).
        Assert.Equal(0.95f, seen.Confidence01);
        Assert.Equal(6.2f, seen.PositionXZ.X, 2);
        Assert.Equal(0.1f, seen.PositionXZ.Y, 2);

        // The other direction must not change: an equal-age, *lower*-confidence sighting does not replace
        // the confident one just because it is scanned last.
        var beliefConfidentFirst = new TeamBelief(TeamId.Home);
        beliefConfidentFirst.RebuildFromObservations(new[] { defender, keeper, opponent },
            new Dictionary<int, FootballerObservation>
            {
                [keeper.Id] = Observation(keeper, keeperSighting),
                [defender.Id] = Observation(defender, defenderSighting)
            });

        Assert.Equal(0.95f, beliefConfidentFirst.Opponents[0].Confidence01);
        Assert.Equal(6.2f, beliefConfidentFirst.Opponents[0].PositionXZ.X, 2);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-06 — the raised estimate ignored uncertainty, dropped the lane metric and under-penalised blocks
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A stale sighting of a defender at the landing must be charged against a raised ball exactly as the
    /// ground estimate charges it. Before the fix <c>EstimateRaised</c> never read
    /// <see cref="PerceivedEntity.PositionUncertaintyMeters"/>, so a remembered defender read as safe as a
    /// freshly seen one.
    /// </summary>
    [Fact]
    public void AUD06_EstimateRaised_ChargesDefenderUncertainty()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));
        FootballerObservation withoutDefender = Observation(self, receiver);

        // Learn the delivery's own landing and flight from the model, then place the defender where his best
        // run reaches the landing at almost exactly the ball's flight time — the only band where the arrival
        // term is not already clamped at 0 or 1 and an uncertainty charge can change the answer.
        LoftedPassTerms open = Model.EstimateLofted(withoutDefender, receiver, 20f, 1f);
        Assert.True(open.Feasible);
        Vector2 landing = open.LandingXZ;
        float defenderOffset = DefenderArrivalOffset(landing, open.FlightTimeSeconds);
        Assert.True(defenderOffset > 0f, "no defender position puts his arrival inside the contested band");

        PerceivedEntity fresh = BlockingEntity(14, landing + new Vector2(0f, defenderOffset));
        PerceivedEntity stale = fresh with { PositionUncertaintyMeters = 3f };
        Assert.Equal(0f, fresh.PositionUncertaintyMeters);

        LoftedPassTerms known = Model.EstimateRaised(
            Observation(self, receiver, fresh), receiver, landing, RaisedPassProfile.Lofted, 1f);
        LoftedPassTerms remembered = Model.EstimateRaised(
            Observation(self, receiver, stale), receiver, landing, RaisedPassProfile.Lofted, 1f);

        Assert.True(known.Feasible && remembered.Feasible);
        Assert.Equal(0f, known.DefenderUncertaintySeconds);
        // Pre-fix: this was 0 and the two completions were identical.
        Assert.True(remembered.DefenderUncertaintySeconds > 0f,
            $"a stale sighting charged {remembered.DefenderUncertaintySeconds:F3}s of uncertainty");
        Assert.True(remembered.CompletionProbability01 < known.CompletionProbability01 - 1e-4f,
            $"an uncertain sighting ({remembered.CompletionProbability01:F3}) must not read as safe as a " +
            $"fresh one ({known.CompletionProbability01:F3})");
    }

    /// <summary>
    /// The caller's lane clearance must reach the raised completion. Before the fix it was clamped and then
    /// overwritten (<c>lane = laneBlocked ? 0f : 1f</c>), so a crowded corridor and an empty one scored the
    /// same raised ball identically.
    /// </summary>
    [Fact]
    public void AUD06_EstimateRaised_UsesTheCallersLaneClearance()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));
        FootballerObservation observation = Observation(self, receiver);

        LoftedPassTerms openLane = Model.EstimateLofted(observation, receiver, 20f, laneClearance01: 1f);
        LoftedPassTerms crowdedLane = Model.EstimateLofted(observation, receiver, 20f, laneClearance01: 0f);

        Assert.True(openLane.Feasible && crowdedLane.Feasible);
        Assert.False(openLane.LaneBlocked);
        Assert.False(crowdedLane.LaneBlocked);
        Assert.Equal(1f, openLane.LaneClearance01);
        Assert.Equal(0f, crowdedLane.LaneClearance01);
        // Pre-fix: both calls returned the same completion because the metric was discarded.
        Assert.True(crowdedLane.CompletionProbability01 < openLane.CompletionProbability01 - 1e-3f,
            $"a crowded lane ({crowdedLane.CompletionProbability01:F3}) scored like an open one " +
            $"({openLane.CompletionProbability01:F3})");
    }

    /// <summary>
    /// A low driven ball straight into a defender is a blocked pass, not a 0.5-probability one. Before the
    /// fix the height-aware block test only zeroed the 30 % lane term, so a driven ball's completion stayed
    /// around 0.70 × 0.86 of the receiver term; it now takes the ground estimate's hard cap.
    /// </summary>
    [Fact]
    public void AUD06_BlockedDrivenBall_TakesTheGroundCompletionCap()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));

        // The driven profile is timed to a promise, so find a promise its own power solve can honour at a
        // point where the lane test would see a body in the ball's low path.
        float promiseSeconds = 0f;
        float drivenPower = 0f;
        for (float candidate = 0.8f; candidate <= 2.0f; candidate += 0.05f)
        {
            if (!RaisedPassModel.Default.TryPowerForArrival(
                    RaisedPassProfile.Driven, 20f, candidate, out float power, out _, out _))
            {
                continue;
            }

            if (FirstPlayableHeightAlong(RaisedPassProfile.Driven, power, targetDistance: 20f) <= 0f) continue;

            promiseSeconds = candidate;
            drivenPower = power;
            break;
        }

        Assert.True(promiseSeconds > 0f, "no driven power honours a 20 m arrival with a playable low point");

        float blockerAlong = FirstPlayableHeightAlong(RaisedPassProfile.Driven, drivenPower, targetDistance: 20f);
        Assert.True(blockerAlong > 0f, "no point of the driven flight is low enough for a defender to play it");

        PerceivedEntity blocker = BlockingEntity(14, new Vector2(blockerAlong, 0.2f));
        var promise = new PassArrivalPromise(
            new Vector2(20f, 0f), promiseSeconds, MeetRadiusMeters: 3f, Confidence01: 0.85f);
        LoftedPassTerms terms = Model.EstimateRaised(
            Observation(self, receiver, blocker), receiver, new Vector2(20f, 0f),
            RaisedPassProfile.Driven, 1f, promise);

        Assert.True(terms.Feasible);
        Assert.True(terms.LaneBlocked,
            $"a defender {blockerAlong:F1} m into a driven ball's path, 0.2 m off the line, was not seen as a block");
        // Pre-fix: completion was receiverTerm * 0.70 * 0.86 ≈ 0.51 because only the lane term was zeroed.
        Assert.True(terms.CompletionProbability01 <= PassSuccessModel.BlockedLaneCompletionCap01 + 1e-4f,
            $"a blocked driven ball still scored {terms.CompletionProbability01:F3}");
        Assert.True(terms.CompletionProbability01 >= PassSuccessModel.MinCompletion01);
    }

    /// <summary>
    /// The same defect on the other raised profiles: a whipped cross that passes low enough for a defender to
    /// play takes the raised cap, not only a zeroed lane term. Before the fix a blocked cross could still
    /// report ~0.48.
    /// </summary>
    [Fact]
    public void AUD06_BlockedCross_KeepsOnlyTheRaisedCompletionCap()
    {
        FootballerState self = Player(8, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);
        PerceivedEntity receiver = Mate(9, new Vector2(20f, 0f));

        Assert.True(RaisedPassModel.Default.TryPowerForLanding(
            RaisedPassProfile.Cross, 20f, out float power, out _, out _));
        float blockerAlong = FirstPlayableHeightAlong(RaisedPassProfile.Cross, power, targetDistance: 20f);
        Assert.True(blockerAlong > 0f, "no point of the cross flight is low enough for a defender to play it");

        PerceivedEntity blocker = BlockingEntity(14, new Vector2(blockerAlong, 0.2f));
        LoftedPassTerms terms = Model.EstimateRaised(
            Observation(self, receiver, blocker), receiver, new Vector2(20f, 0f),
            RaisedPassProfile.Cross, 1f);

        Assert.True(terms.Feasible);
        Assert.True(terms.LaneBlocked,
            $"a defender {blockerAlong:F1} m into the cross, 0.2 m off the line, was not seen as a block");
        // Pre-fix: completion was receiverTerm * 0.70 * 0.68 ≈ 0.48.
        Assert.True(terms.CompletionProbability01 <= PassSuccessModel.BlockedRaisedCompletionCap01 + 1e-4f,
            $"a blocked cross still scored {terms.CompletionProbability01:F3}");
        Assert.True(terms.CompletionProbability01 >= PassSuccessModel.MinCompletion01);
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-09 — the local pressure field ignored a rushing goalkeeper
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An opposing keeper sweeping out is an opponent. Before the fix <c>CollectOpponents</c> skipped the
    /// goalkeeper role outright, so the carrier's pressure field reported the ground he was closing into as
    /// completely empty. The second half of the test is the control for the claim that existing distance
    /// filtering still keeps a keeper parked on his own goal line out of the answer.
    /// </summary>
    [Fact]
    public void AUD09_PressureField_CountsARushingGoalkeeper()
    {
        FootballerState self = Player(1, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero);

        // The keeper has swept 4 m off his line and is closing on the carrier at 3 m/s.
        PerceivedEntity rushingKeeper = new(
            12, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(4f, 0f, 0f), new Vector3(-3f, 0f, 0f), 0.95f);
        var field = new LocalPressureField();
        Assert.True(field.TryBuild(Observation(self, rushingKeeper), Vector2.Zero));

        float pressure = field.PressureAt(new Vector2(2f, 0f));
        PressureCell cell = field.Read(new Vector2(2f, 0f), out bool inside);
        Assert.True(inside);
        // Pre-fix: the keeper was skipped, so this cell reported pressure 0 and no opponent arrival.
        Assert.True(pressure > 0.2f, $"pressure from a rushing keeper was {pressure:F3}");
        Assert.True(cell.OpponentArrivalSeconds >= 0f,
            "the keeper was not given an arrival time at the cell he is closing into");

        // Control: a keeper parked 48 m away (his own goal line) cannot reach any cell inside the 1.5 s
        // horizon, so including goalkeepers must not paint pressure from a keeper who is not part of the play.
        PerceivedEntity parkedKeeper = rushingKeeper with
        {
            EstimatedPosition = new Vector3(48f, 0f, 0f),
            EstimatedVelocity = Vector3.Zero
        };
        var parkedField = new LocalPressureField();
        Assert.True(parkedField.TryBuild(Observation(self, parkedKeeper), Vector2.Zero));
        Assert.Equal(0f, parkedField.PressureAt(new Vector2(2f, 0f)));
    }

    // ---------------------------------------------------------------------------------------------
    // AUD-11 — hash order decided between equidistant stale opponents
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Two stale opponents equidistant from the scan origin must not be chosen by <c>Dictionary</c> slot
    /// order. The audit's trigger is <c>Forget</c> (a sending-off or substitution) changing that order; this
    /// fixture builds the same two beliefs in both insertion orders and requires the same, lower-id answer.
    /// </summary>
    [Fact]
    public void AUD11_PlayerMemory_EquidistantStaleBeliefs_TieBreakOnPlayerId()
    {
        PerceivedEntity first = FindStale(observeLowerIdFirst: true);
        PerceivedEntity second = FindStale(observeLowerIdFirst: false);

        // Pre-fix: each memory returned whichever belief happened to be inserted first (#12 then #20).
        Assert.Equal(first.PlayerId, second.PlayerId);
        Assert.Equal(12, first.PlayerId);
    }

    private static PerceivedEntity FindStale(bool observeLowerIdFirst)
    {
        var memory = new PlayerMemory();
        Vector3 left = new(-5f, 0f, 0f);
        Vector3 right = new(5f, 0f, 0f);
        const float confidence = 0.9f;

        if (observeLowerIdFirst)
        {
            memory.Observe(12, TeamId.Away, TacticalRole.CenterBack, left, Vector3.Zero, confidence, tick: 1);
            memory.Observe(20, TeamId.Away, TacticalRole.CenterBack, right, Vector3.Zero, confidence, tick: 1);
        }
        else
        {
            memory.Observe(20, TeamId.Away, TacticalRole.CenterBack, right, Vector3.Zero, confidence, tick: 1);
            memory.Observe(12, TeamId.Away, TacticalRole.CenterBack, left, Vector3.Zero, confidence, tick: 1);
        }

        Assert.True(memory.TryFindStaleOpponent(
            TeamId.Home, nowTick: 2, from: Vector3.Zero, minAgeTicks: 0, maxRangeMeters: 50f, out PerceivedEntity found));
        return found;
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static PassSuccessModel Model => new(new PassPlanner(new PitchDimensions()));

    private static FootballerState Player(int id, TeamId team, TacticalRole role, Vector3 position) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = PlayerTendencyProfile.ForRole(role),
        Position = position,
        FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f)
    };

    private static PerceivedEntity Sighting(int playerId, Vector3 position, float confidence01, int ageTicks) => new(
        playerId, TeamId.Away, TacticalRole.Striker, position, Vector3.Zero, confidence01)
    {
        ObservedAgeTicks = ageTicks
    };

    private static PerceivedEntity Mate(int playerId, Vector2 position) => new(
        playerId, TeamId.Home, TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    private static PerceivedEntity BlockingEntity(int playerId, Vector2 position) => new(
        playerId, TeamId.Away, TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y), Vector3.Zero, 0.95f)
    {
        Capability = CapabilityEstimate.Generic
    };

    /// <summary>
    /// Distance from <paramref name="landing"/> at which a stationary defender's best run arrives just inside
    /// the arrival term's unclamped band, so a stale-sighting uncertainty charge is observable. Returns 0 when
    /// no such position exists.
    /// </summary>
    private static float DefenderArrivalOffset(Vector2 landing, float flightSeconds)
    {
        for (float offset = 3f; offset <= 25f; offset += 0.25f)
        {
            ReachEstimate reach = TimeToReachModel.ToPoint(
                ReachKinematics.FromEntity(BlockingEntity(14, landing + new Vector2(0f, offset))),
                landing, horizonSeconds: 5f);
            if (!reach.Reachable) continue;

            float arrival = reach.Seconds + PassSuccessModel.OpponentReactionSeconds;
            if (MathF.Abs(arrival - flightSeconds) <= 0.15f) return offset;
        }

        return 0f;
    }

    /// <summary>
    /// First point along the flight (outside the launch and landing-contest zones) where the calibrated
    /// trajectory is low enough for a defender standing there to play the ball. Returns 0 when the whole
    /// tested extent is above <see cref="PassSuccessModel.AirborneBlockHeightMeters"/>.
    /// </summary>
    private static float FirstPlayableHeightAlong(RaisedPassProfile profile, float power01, float targetDistance)
    {
        float limit = targetDistance - PassSuccessModel.AirborneLandingContestMeters - 1f;
        for (float along = 1.5f; along <= limit; along += 0.5f)
        {
            float height = RaisedPassModel.Default.HeightAtDistance(profile, power01, along);
            if (height <= PassSuccessModel.AirborneBlockHeightMeters - 0.3f) return along;
        }

        return 0f;
    }

    private static FootballerObservation Observation(FootballerState self, params PerceivedEntity[] visible) =>
        Observation(self.Id, self.Team, self.Role, self.Position, visible);

    private static FootballerObservation Observation(
        int selfId, TeamId selfTeam, TacticalRole selfRole, Vector3 selfPosition, params PerceivedEntity[] visible) => new()
    {
        SelfId = selfId,
        SelfTeam = selfTeam,
        SelfRole = selfRole,
        SelfPosition = selfPosition,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(selfTeam == TeamId.Home ? 1f : -1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = selfPosition,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = selfTeam,
        BallLastTouchedByPlayerId = selfId,
        TacticalAnchorPosition = selfPosition,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 100,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };
}
