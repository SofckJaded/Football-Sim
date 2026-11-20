using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A7.1 — the perception side of scanning: the sight cone follows the head, a body on the sight line hides
/// a distant opponent, sightings still land in the A2.2 memory, and a caller that supplies no configuration
/// keeps the pre-A7.1 model exactly.
///
/// The exit gate's clause lives here: <i>scanning can reveal a rear threat before reception; lack of a scan
/// leaves an aged/uncertain estimate</i>. The two worlds differ only in the head — same players, same ball,
/// same seed — so the difference in the observation is the gaze.
/// </summary>
public sealed class ScanPerceptionTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private const int SelfId = 2;      // even id: the first danger scan uses the + shoulder
    private const int RearId = 21;
    private const int TeammateId = 3;
    private const int BlockerId = 22;

    [Fact]
    public void AScanBeforeReception_RevealsARearThreat_BeforeTheBallArrives()
    {
        (List<FootballerObservation> scanned, ScanTelemetry scanStats, int scanTick) =
            RunReceivingWorld(scanning: true, passInFlight: true);

        // The first look sees everybody; from tick 2 the rear threat is behind the body.
        Assert.Equal(0, Find(scanned[0], RearId)!.Value.ObservedAgeTicks);
        Assert.True(scanTick > 1 && scanTick <= 25,
            $"the head took {scanTick} ticks to re-acquire a rear threat 8 m away");

        FootballerObservation revelation = scanned[scanTick - 1];
        PerceivedEntity rear = Find(revelation, RearId)!.Value;
        Assert.Equal(0, rear.ObservedAgeTicks);
        Assert.Equal(0f, rear.PositionUncertaintyMeters);
        Assert.Equal(ScanAttentionKind.DangerSpace, revelation.Scan.Attention);
        Assert.True(revelation.Scan.HeadYawDegrees > 50f,
            $"the gaze only turned {revelation.Scan.HeadYawDegrees:F0}°");
        Assert.True(Vector2.Dot(revelation.EffectiveGazeXZ, revelation.SelfFacingXZ) < 0.5f,
            "the gaze did not move away from the body facing");

        // The head completed the glance (the turn brought the threat into the cone; the hold fixes it).
        bool completedScan = false;
        foreach (FootballerObservation look in scanned) completedScan |= look.Scan.IsScanning;
        Assert.True(completedScan, "the gaze revealed the threat but no scan was ever completed");

        // Scanning cost something and was measurable.
        Assert.True(scanStats.ScanStarts > 0);
        Assert.True(scanStats.ScanTicks > 0);
        Assert.True(scanStats.RevealedByGaze > 0, "no observation was attributed to the gaze");

        // The control: the same world without scanning only ever has an aged estimate.
        (List<FootballerObservation> unscanned, ScanTelemetry unscannedStats, _) =
            RunReceivingWorld(scanning: false, passInFlight: true);
        for (int index = 1; index < unscanned.Count; index++)
        {
            PerceivedEntity remembered = Find(unscanned[index], RearId)!.Value;
            Assert.True(remembered.ObservedAgeTicks >= index,
                $"tick {index + 1}: an unscanned player still had a fresh rear sighting");
        }

        Assert.Equal(0, unscannedStats.ScanStarts);
        Assert.Equal(0, unscannedStats.RevealedByGaze);
    }

    [Fact]
    public void WithoutAPassInFlight_EvenAScanningPlayerKeepsTheAgedEstimate()
    {
        // The same world and the same configuration, differing only in the ball's state: the utility rule
        // is what triggers the scan, not the feature flag.
        (List<FootballerObservation> looks, ScanTelemetry stats, _) =
            RunReceivingWorld(scanning: true, passInFlight: false);

        for (int index = 1; index < looks.Count; index++)
        {
            PerceivedEntity remembered = Find(looks[index], RearId)!.Value;
            Assert.True(remembered.ObservedAgeTicks >= index,
                $"tick {index + 1}: a player scanned without a reception to prepare for");
        }

        Assert.Equal(0, stats.ScanStarts);
    }

    [Fact]
    public void ABodyOnTheSightLine_HidesADistantOpponent_ButNotNearPlayersOrTeammates()
    {
        var blocked = RunSightLine(new Vector3(6f, 0f, 0.2f));
        var clear = RunSightLine(new Vector3(6f, 0f, 3f));

        Assert.True(Find(blocked.Observation, RearId)!.Value.ObservedAgeTicks > 0,
            "a body 0.2 m off a 6 m sight line did not hide the opponent 20 m away");
        Assert.Equal(0, Find(clear.Observation, RearId)!.Value.ObservedAgeTicks);

        // A teammate behind the same body is coordinated, not seen: never hidden.
        Assert.Equal(0, Find(blocked.Observation, TeammateId)!.Value.ObservedAgeTicks);

        // The near field is contact and voice: a body does not hide a man standing on your toes.
        Assert.Equal(0, Find(blocked.Observation, BlockerId)!.Value.ObservedAgeTicks);

        Assert.True(blocked.Stats.OccludedByBody > 0);
        Assert.Equal(0L, clear.Stats.OccludedByBody);
    }

    [Fact]
    public void ScanningOff_ReproducesTheStandaloneSightModelExactly()
    {
        // Two identical worlds: one perception with no configuration (the pre-A7.1 default) and one with an
        // explicit configuration whose scanning is off. They must agree tick for tick, RNG draw for draw.
        var standalone = new PerceptionSystem();
        var configuredOff = new PerceptionSystem(null, new AiRuntimeConfig { EnableScanning = false });
        var standaloneRng = new SeededRandom(4242);
        var configuredRng = new SeededRandom(4242);

        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.Winger, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState rear = Player(RearId, TeamId.Away, TacticalRole.CenterBack, new Vector3(-8f, 0f, 0f),
            new Vector2(-1f, 0f));
        BallState ball = Ball();
        var possession = new BallPossessionSnapshot(BallControlState.InFlightPass, TeamId.Home, null, SelfId);
        FootballerState[] players = { self, rear };

        for (int tick = 1; tick <= 40; tick++)
        {
            FootballerObservation a = Observe(standalone, self, players, ball, standaloneRng, tick, possession);
            FootballerObservation b = Observe(configuredOff, self, players, ball, configuredRng, tick, possession);

            Assert.Equal(ScanStatus.None, a.Scan);
            Assert.Equal(a.Scan, b.Scan);
            Assert.Equal(a.SelfFacingXZ, b.SelfFacingXZ);
            Assert.Equal(a.EffectiveGazeXZ, b.EffectiveGazeXZ);
            Assert.Equal(a.KnowledgeAgeTicks, b.KnowledgeAgeTicks);
            Assert.Equal(a.VisiblePlayers.Count, b.VisiblePlayers.Count);
            for (int i = 0; i < a.VisiblePlayers.Count; i++)
                Assert.Equal(a.VisiblePlayers[i], b.VisiblePlayers[i]);
        }
    }

    [Fact]
    public void ScanningAndOcclusion_AreDeterministic()
    {
        (List<FootballerObservation> left, _, _) = RunReceivingWorld(scanning: true, passInFlight: true);
        (List<FootballerObservation> right, _, _) = RunReceivingWorld(scanning: true, passInFlight: true);

        Assert.Equal(left.Count, right.Count);
        for (int tick = 0; tick < left.Count; tick++)
        {
            FootballerObservation a = left[tick];
            FootballerObservation b = right[tick];
            Assert.Equal(a.Scan, b.Scan);
            Assert.Equal(a.KnowledgeAgeTicks, b.KnowledgeAgeTicks);
            Assert.Equal(a.VisiblePlayers.Count, b.VisiblePlayers.Count);
            for (int i = 0; i < a.VisiblePlayers.Count; i++)
                Assert.Equal(a.VisiblePlayers[i], b.VisiblePlayers[i]);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>
    /// A receiver facing +X with the ball arriving from the front and one opponent 8 m behind him. When
    /// <paramref name="scanning"/> is false the perception has no configuration at all — the pre-A7.1 model.
    /// </summary>
    private static (List<FootballerObservation> Looks, ScanTelemetry Stats, int ScanTick) RunReceivingWorld(
        bool scanning,
        bool passInFlight,
        int ticks = 40)
    {
        PerceptionSystem perception = scanning
            ? new PerceptionSystem(null, new AiRuntimeConfig())
            : new PerceptionSystem();
        var rng = new SeededRandom(1234);
        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.Winger, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState rear = Player(RearId, TeamId.Away, TacticalRole.CenterBack, new Vector3(-8f, 0f, 0f),
            new Vector2(-1f, 0f));
        BallState ball = Ball();
        FootballerState[] players = { self, rear };
        BallPossessionSnapshot possession = passInFlight
            ? new BallPossessionSnapshot(BallControlState.InFlightPass, TeamId.Home, null, SelfId)
            : BallPossessionSnapshot.Loose;

        var looks = new List<FootballerObservation>(ticks);
        int scanTick = -1;
        for (int tick = 1; tick <= ticks; tick++)
        {
            FootballerObservation observation = Observe(perception, self, players, ball, rng, tick, possession);
            looks.Add(observation);
            if (tick > 1 && scanTick < 0)
            {
                PerceivedEntity? remembered = Find(observation, RearId);
                if (remembered is { ObservedAgeTicks: 0 }) scanTick = tick;
            }
        }

        return (looks, perception.ScanStats, scanTick);
    }

    /// <summary>
    /// One observer facing +X with a target 20 m away and a body whose off-line distance is set by
    /// <paramref name="blockerPosition"/>; plus a teammate on the same line and a near opponent.
    /// </summary>
    private static (FootballerObservation Observation, ScanTelemetry Stats) RunSightLine(Vector3 blockerPosition)
    {
        var perception = new PerceptionSystem(null, new AiRuntimeConfig());
        var rng = new SeededRandom(99);
        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero,
            new Vector2(1f, 0f));
        FootballerState target = Player(RearId, TeamId.Away, TacticalRole.Striker, new Vector3(20f, 0f, 0f),
            new Vector2(-1f, 0f));
        FootballerState teammate = Player(TeammateId, TeamId.Home, TacticalRole.Winger, new Vector3(20f, 0f, 0.6f),
            new Vector2(-1f, 0f));
        FootballerState blocker = Player(BlockerId, TeamId.Away, TacticalRole.CenterBack, blockerPosition,
            new Vector2(-1f, 0f));
        FootballerState nearOpponent = Player(BlockerId + 1, TeamId.Away, TacticalRole.Winger,
            new Vector3(1.8f, 0f, -1.5f), new Vector2(-1f, 0f));
        FootballerState[] players = { self, target, teammate, blocker, nearOpponent };
        BallState ball = Ball();

        FootballerObservation? observation = null;
        for (int tick = 1; tick <= 5; tick++)
            observation = Observe(perception, self, players, ball, rng, tick, possession: null);

        return (observation!, perception.ScanStats);
    }

    private static FootballerObservation Observe(
        PerceptionSystem perception,
        FootballerState self,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        SeededRandom rng,
        int tick,
        BallPossessionSnapshot? possession) =>
        perception.BuildObservation(
            self, players, ball, possession?.PossessionTeam, MatchPhase.FirstHalf, tick * Dt,
            attackingDirX: 1f, isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, rng,
            possession, tick: tick);

    private static FootballerState Player(
        int id,
        TeamId team,
        TacticalRole role,
        Vector3 position,
        Vector2 facing,
        Vector3 velocity = default) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies(),
        Position = position,
        Velocity = velocity,
        FacingXZ = facing
    };

    private static BallState Ball() => new()
    {
        Position = new Vector3(8f, 0.11f, 0f),
        Velocity = new Vector3(-3f, 0f, 0f)
    };

    private static PerceivedEntity? Find(in FootballerObservation observation, int playerId)
    {
        foreach (PerceivedEntity entity in observation.VisiblePlayers)
            if (entity.PlayerId == playerId) return entity;
        return null;
    }
}
