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
/// A9.2 — the two perception ablations. The point of a switch is not that it exists but that the same world
/// answers differently when it is off, so every test below runs one fixture twice and compares the pictures.
///
/// <list type="bullet">
/// <item><b>orientation</b> (<see cref="AiRuntimeConfig.EnableOrientation"/>) — a man behind the observer is
/// an aged memory prediction when the facing/gaze gate is on, and is measured live when the gate is off,
/// while a body on the sight line still hides him.</item>
/// <item><b>perception limits</b> (<see cref="AiRuntimeConfig.EnablePerceptionLimits"/>) — off is the
/// pre-A2.2 model: every active opponent is measured from the live world every tick, so neither the cone
/// nor a body on the sight line removes anybody.</item>
/// </list>
///
/// The two switches are deliberately not the same gate, and the sight-line test shows the difference: the
/// orientation ablation keeps the occlusion that the limits ablation removes. Both default on, so a shipped
/// configuration takes exactly the branches it took before A9.2 — a null configuration means "on" too, which
/// is what keeps the standalone fixtures that build <c>new PerceptionSystem()</c> on the limited model.
/// </summary>
public sealed class A9PerceptionAblationTests
{
    private const float Dt = SimConstants.FixedDeltaTime;

    /// <summary>Even id: the danger-scan shoulder seed is deterministic from it (unused here — no scan fires).</summary>
    private const int SelfId = 2;
    private const int OpponentId = 21;
    private const int TeammateId = 3;
    private const int BlockerId = 22;

    private const int RearWorldTicks = 6;
    private const int SightLineTicks = 5;

    /// <summary>
    /// 8 m behind the observer, ambling away when first seen. The fixture is the same in every test that uses
    /// it, so the only difference between two runs is the configuration handed to the perception system.
    /// </summary>
    private static readonly Vector3 RearStart = new(-8f, 0f, 0f);
    private static readonly Vector3 RearStartVelocity = new(-0.5f, 0f, 0f);

    /// <summary>Sprint pace from the second tick on: memory can only extrapolate the pace it last saw.</summary>
    private const float RearSprintVelocity = -6f;

    [Fact]
    public void OrientationOff_MeasuresTheManBehindTheObserver_TheDefaultKeepsAnAgedPrediction()
    {
        var orientationOn = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig()));
        var orientationOff = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig { EnableOrientation = false }));

        // On: the first look sees everybody; from then on the man behind the body is published from memory,
        // with the age growing one tick at a time, growing uncertainty, and a position that lags the world.
        AssertAgedRearPrediction(orientationOn.Looks, orientationOn.LivePositions, "orientation on");
        Assert.Equal(0L, orientationOn.Stats.OccludedByBody);
        Assert.Equal(0L, orientationOn.Stats.RevealedByGaze);

        // Off: identical world, identical players, identical seed — and now he is measured every tick. If the
        // switch were ignored this run would reproduce the aged prediction above, exactly.
        AssertLiveRearSightings(orientationOff.Looks, orientationOff.LivePositions, "orientation off");
        Assert.Equal(0L, orientationOff.Stats.OccludedByBody);
        Assert.Equal(0L, orientationOff.Stats.RevealedByGaze);
    }

    [Fact]
    public void PerceptionLimitsOff_PublishesEveryOpponentFromTheLiveWorld()
    {
        var limited = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig()));
        var ablated = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig { EnablePerceptionLimits = false }));

        // On: the shipped knowledge boundary — the man behind the body is a prediction, not a measurement.
        AssertAgedRearPrediction(limited.Looks, limited.LivePositions, "limits on");

        // Off: the pre-A2.2 model — no cone, no memory prediction path. Every active opponent is measured
        // this tick, so the age is zero and the estimate is the live position (plus the fresh-sighting
        // distance noise), even though the observer never turns his head.
        AssertLiveRearSightings(ablated.Looks, ablated.LivePositions, "limits off");

        // The limits ablation also removes the occlusion path (a body cannot hide a man) and the gaze, so
        // neither counter may report anything.
        Assert.Equal(0L, ablated.Stats.OccludedByBody);
        Assert.Equal(0L, ablated.Stats.RevealedByGaze);
    }

    [Fact]
    public void PerceptionLimitsOff_NoBodyOccludesTheSightLine_ButOrientationOffStillDoes()
    {
        // One observer facing +X, an opponent 20 m directly ahead and a body 0.2 m off the sight line 6 m
        // out. Distance, cone and first look are identical between the three runs; only the switches differ.
        var limited = RunSightLine(new PerceptionSystem(null, new AiRuntimeConfig()));
        var limitsOff = RunSightLine(new PerceptionSystem(null, new AiRuntimeConfig { EnablePerceptionLimits = false }));
        var orientationOff = RunSightLine(new PerceptionSystem(null, new AiRuntimeConfig { EnableOrientation = false }));

        // Shipped model: the body hides the distant opponent from the second tick on.
        PerceivedEntity hidden = Require(limited.Observation, OpponentId, "limits on");
        Assert.Equal(SightLineTicks - 1, hidden.ObservedAgeTicks);
        Assert.Equal(SightLineTicks - 1, limited.Observation.KnowledgeAgeTicks);
        Assert.True(limited.Stats.OccludedByBody > 0, "the shipped model did not count the deferral at all");

        // Limits ablated: the same body hides nobody — the opponent is measured this tick, at his live
        // position, with no uncertainty and no memory age on the observation.
        PerceivedEntity visible = Require(limitsOff.Observation, OpponentId, "limits off");
        Assert.Equal(0, visible.ObservedAgeTicks);
        Assert.Equal(0f, visible.PositionUncertaintyMeters);
        Assert.Equal(0, limitsOff.Observation.KnowledgeAgeTicks);
        Assert.Equal(0L, limitsOff.Stats.OccludedByBody);
        Assert.True(Vector3.Distance(visible.EstimatedPosition, new Vector3(20f, 0f, 0f)) < 0.60f,
            $"the ablated picture did not publish the live position: {visible.EstimatedPosition}");

        // Orientation off is not the limits ablation: the cone is gone (he is directly in front anyway) but
        // the body on the sight line still hides him. This is the sentence that keeps the two gates apart.
        PerceivedEntity stillHidden = Require(orientationOff.Observation, OpponentId, "orientation off");
        Assert.Equal(SightLineTicks - 1, stillHidden.ObservedAgeTicks);
        Assert.True(orientationOff.Stats.OccludedByBody > 0,
            "the orientation ablation also removed occlusion, which is the limits ablation's job");
    }

    [Fact]
    public void BothPerceptionSwitches_DefaultOn_AndDescribeNamesThem()
    {
        var fresh = new AiRuntimeConfig();
        AiRuntimeConfig defaults = AiRuntimeConfig.Default;

        Assert.True(fresh.EnablePerceptionLimits);
        Assert.True(fresh.EnableOrientation);
        Assert.True(defaults.EnablePerceptionLimits);
        Assert.True(defaults.EnableOrientation);

        // The artifact's description states which build produced its numbers: A9.2's whole point.
        string on = defaults.Describe();
        Assert.Contains("percept(on)", on);
        Assert.Contains("orient(on)", on);
        string off = new AiRuntimeConfig { EnablePerceptionLimits = false, EnableOrientation = false }.Describe();
        Assert.Contains("percept(off)", off);
        Assert.Contains("orient(off)", off);

        // Both ablations are supported configurations (the same-seed A/B needs them), not validation errors.
        AiRuntimeConfigValidation.Validate(new AiRuntimeConfig
        {
            EnablePerceptionLimits = false,
            EnableOrientation = false
        });
    }

    [Fact]
    public void NullConfiguration_MeansBothSwitchesOn_NotTheAblation()
    {
        // Standalone fixtures and probes build PerceptionSystem() with no configuration: scanning and the
        // cognitive error model stay opt-in there, but the two A9.2 switches are ON, so the standalone model
        // is the limited one. A null configuration must not silently become the ablation.
        var standalone = RunRearWorld(new PerceptionSystem());
        AssertAgedRearPrediction(standalone.Looks, standalone.LivePositions, "null configuration");

        var ablated = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig
        {
            EnablePerceptionLimits = false,
            EnableOrientation = false
        }));
        AssertLiveRearSightings(ablated.Looks, ablated.LivePositions, "null-is-off hypothesis");

        // The scanning-off configuration is the standalone model tick for tick, so the equality above is the
        // pre-A7.1 model with both A9.2 switches on, not a coincidence of the fixture.
        var scanningOff = RunRearWorld(new PerceptionSystem(null, new AiRuntimeConfig { EnableScanning = false }));
        Assert.Equal(standalone.Looks.Count, scanningOff.Looks.Count);
        for (int index = 0; index < standalone.Looks.Count; index++)
        {
            Assert.Equal(standalone.Looks[index].KnowledgeAgeTicks, scanningOff.Looks[index].KnowledgeAgeTicks);
            Assert.Equal(standalone.Looks[index].VisiblePlayers.Count, scanningOff.Looks[index].VisiblePlayers.Count);
            for (int member = 0; member < standalone.Looks[index].VisiblePlayers.Count; member++)
                Assert.Equal(standalone.Looks[index].VisiblePlayers[member], scanningOff.Looks[index].VisiblePlayers[member]);
        }
    }

    // ---- assertions shared by more than one world -------------------------------------------------

    /// <summary>
    /// The shipped knowledge boundary: only the first look sees the man behind the body; every later tick is
    /// a prediction from <see cref="PlayerMemory"/> whose age is the tick difference, whose uncertainty has
    /// grown past a fresh sighting's, whose confidence has decayed, and whose position falls behind an
    /// opponent whose pace changed after the sighting (memory can only extrapolate the velocity it saw).
    /// </summary>
    private static void AssertAgedRearPrediction(
        IReadOnlyList<FootballerObservation> looks,
        IReadOnlyList<Vector3> livePositions,
        string because)
    {
        PerceivedEntity firstLook = Require(looks[0], OpponentId, because);
        Assert.Equal(0, firstLook.ObservedAgeTicks);
        Assert.Equal(0f, firstLook.PositionUncertaintyMeters);

        for (int index = 1; index < looks.Count; index++)
        {
            int tick = index + 1;
            PerceivedEntity remembered = Require(looks[index], OpponentId, because);
            Assert.Equal(tick - 1, remembered.ObservedAgeTicks);
            Assert.Equal(tick - 1, looks[index].KnowledgeAgeTicks);
            Assert.True(remembered.PositionUncertaintyMeters > PlayerMemory.BaseUncertaintyMeters,
                $"{because}: tick {tick} published a stale sighting as if it were exact");
            Assert.True(remembered.Confidence01 < firstLook.Confidence01,
                $"{because}: tick {tick} published a stale sighting with undecayed confidence");
        }

        // The last sighting was at the old pace, so the prediction must lag the sprinting live position.
        int last = looks.Count - 1;
        float gap = Vector3.Distance(Require(looks[last], OpponentId, because).EstimatedPosition, livePositions[last]);
        Assert.True(gap > 0.25f,
            $"{because}: the aged estimate was {gap:F2} m from the live position — it tracked the world "
            + "it was supposed to have lost");
    }

    /// <summary>
    /// The ablated model: the same opponent is measured live every tick — zero age (a fresh sighting, not a
    /// memory prediction), zero uncertainty, no knowledge age on the observation, and an estimate within the
    /// fresh-sighting distance noise of the live position.
    /// </summary>
    private static void AssertLiveRearSightings(
        IReadOnlyList<FootballerObservation> looks,
        IReadOnlyList<Vector3> livePositions,
        string because)
    {
        for (int index = 0; index < looks.Count; index++)
        {
            int tick = index + 1;
            PerceivedEntity seen = Require(looks[index], OpponentId, because);
            Assert.Equal(0, seen.ObservedAgeTicks);
            Assert.Equal(0f, seen.PositionUncertaintyMeters);
            Assert.Equal(0, looks[index].KnowledgeAgeTicks);
            float gap = Vector3.Distance(seen.EstimatedPosition, livePositions[index]);
            Assert.True(gap < 0.35f,
                $"{because}: tick {tick} published the opponent {gap:F2} m from his live position");
        }
    }

    // ---- worlds ----------------------------------------------------------------------------------

    /// <summary>
    /// The observer faces +X with an opponent 8 m behind him and a teammate 9 m behind the observer's left
    /// shoulder. The opponent is ambling when first seen and then sprints away, so a memory prediction (which
    /// can only extrapolate the velocity it saw) must fall behind the live position — a live measurement
    /// cannot. The ball is loose and nobody is receiving, so no scan is ever triggered and the only variation
    /// between runs is the perception configuration.
    /// </summary>
    private static (List<FootballerObservation> Looks, List<Vector3> LivePositions, ScanTelemetry Stats)
        RunRearWorld(PerceptionSystem perception, int ticks = RearWorldTicks)
    {
        var rng = new SeededRandom(20261122);
        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.Winger, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState rear = Player(OpponentId, TeamId.Away, TacticalRole.CenterBack, RearStart,
            new Vector2(-1f, 0f), RearStartVelocity);
        FootballerState teammate = Player(TeammateId, TeamId.Home, TacticalRole.Striker, new Vector3(-8f, 0f, 4f),
            new Vector2(-1f, 0f));
        FootballerState[] players = { self, rear, teammate };
        BallState ball = Ball();

        var looks = new List<FootballerObservation>(ticks);
        var livePositions = new List<Vector3>(ticks);
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (tick > 1)
            {
                rear.Velocity = new Vector3(RearSprintVelocity, 0f, 0f);
                rear.Position += new Vector3(rear.Velocity.X * Dt, 0f, 0f);
            }

            livePositions.Add(rear.Position);
            looks.Add(Observe(perception, self, players, ball, rng, tick, possession: null));
        }

        return (looks, livePositions, perception.ScanStats);
    }

    /// <summary>
    /// One observer facing +X, an opponent 20 m ahead, a teammate beside him, a body 0.2 m off the sight line
    /// 6 m out (it hides the distant opponent in the shipped model) and a near opponent inside the near field.
    /// </summary>
    private static (FootballerObservation Observation, ScanTelemetry Stats) RunSightLine(
        PerceptionSystem perception, int ticks = SightLineTicks)
    {
        var rng = new SeededRandom(99);
        FootballerState self = Player(SelfId, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero,
            new Vector2(1f, 0f));
        FootballerState target = Player(OpponentId, TeamId.Away, TacticalRole.Striker, new Vector3(20f, 0f, 0f),
            new Vector2(-1f, 0f));
        FootballerState teammate = Player(TeammateId, TeamId.Home, TacticalRole.Winger, new Vector3(20f, 0f, 0.6f),
            new Vector2(-1f, 0f));
        FootballerState blocker = Player(BlockerId, TeamId.Away, TacticalRole.CenterBack, new Vector3(6f, 0f, 0.2f),
            new Vector2(-1f, 0f));
        FootballerState nearOpponent = Player(BlockerId + 1, TeamId.Away, TacticalRole.Winger,
            new Vector3(1.8f, 0f, -1.5f), new Vector2(-1f, 0f));
        FootballerState[] players = { self, target, teammate, blocker, nearOpponent };
        BallState ball = Ball();

        FootballerObservation? observation = null;
        for (int tick = 1; tick <= ticks; tick++)
            observation = Observe(perception, self, players, ball, rng, tick, possession: null);

        return (observation!, perception.ScanStats);
    }

    // ---- plumbing --------------------------------------------------------------------------------

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

    private static PerceivedEntity Require(in FootballerObservation observation, int playerId, string because)
    {
        foreach (PerceivedEntity entity in observation.VisiblePlayers)
            if (entity.PlayerId == playerId) return entity;

        Assert.Fail($"{because}: player {playerId} is missing from the observation's picture");
        return default;
    }

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
}
