using System;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A2.4 — the local pressure window.
///
/// What is checked here is the contract a scorer depends on: every cell reports finite, bounded numbers;
/// a cell that is out of play says so instead of looking empty; arrival margins have the documented
/// sign; the same question asked twice in a tick costs one build; and an opponent running at a point
/// raises the pressure there more than an opponent running away from it.
/// </summary>
public sealed class PressureFieldTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private const int Self = 1;

    private static FootballerObservation Build(
        Vector3 selfPosition,
        Vector3 selfVelocity,
        Vector2 selfFacing,
        FootballerState[] others,
        int tick,
        PitchDimensions? pitch = null,
        float selfStamina = 1f)
    {
        var self = new FootballerState
        {
            Id = Self,
            Team = TeamId.Home,
            ShirtNumber = 5,
            Role = TacticalRole.CentralMidfielder,
            Attributes = new PlayerAttributes(),
            Tendencies = new PlayerTendencies(),
            Position = selfPosition,
            Velocity = selfVelocity,
            FacingXZ = selfFacing,
            Stamina01 = selfStamina
        };

        var players = new FootballerState[others.Length + 1];
        players[0] = self;
        Array.Copy(others, 0, players, 1, others.Length);

        var perception = new PerceptionSystem();
        var ball = new BallState { Position = new Vector3(45f, 0.11f, 20f) };
        return perception.BuildObservation(
            self, players, ball, null, MatchPhase.FirstHalf, tick * Dt, attackingDirX: 1f,
            isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, new SeededRandom(17),
            tick: tick);
    }

    private static FootballerState Opponent(
        int id,
        Vector3 position,
        Vector3 velocity = default,
        TeamId team = TeamId.Away,
        TacticalRole role = TacticalRole.CenterBack) => new()
    {
        Id = id,
        Team = team,
        ShirtNumber = id,
        Role = role,
        Attributes = new PlayerAttributes(),
        Tendencies = new PlayerTendencies(),
        Position = position,
        Velocity = velocity
    };

    [Fact]
    public void EveryCell_IsFiniteAndBounded()
    {
        var field = new LocalPressureField();
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(6f, 0f, 3f), new Vector3(-1f, 0f, 0f)) }, tick: 10);

        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));
        Assert.Equal(LocalPressureField.CellCount, field.Cells.Count);

        foreach (PressureCell cell in field.Cells)
        {
            Assert.True(float.IsFinite(cell.CentreXZ.X) && float.IsFinite(cell.CentreXZ.Y));
            Assert.InRange(cell.Pressure01, 0f, 1f);
            Assert.InRange(cell.InfluenceShare, 0f, 1f);
            Assert.InRange(cell.OpponentInfluenceShare, 0f, 1f);
            Assert.InRange(cell.UnclaimedShare, 0f, 1f);
            // A2.5: the three shares partition the cell, and the unclaimed part is explicit.
            Assert.Equal(1f, cell.InfluenceShare + cell.OpponentInfluenceShare + cell.UnclaimedShare, precision: 3);
            Assert.True(float.IsFinite(cell.ArrivalMarginSeconds));
            Assert.False(cell.IsBoundaryCell);
        }
    }

    [Fact]
    public void BoundaryCells_AreExplicit_NotSilentlyEmpty()
    {
        var pitch = new PitchDimensions();
        var field = new LocalPressureField();
        // A player standing on the touchline: half the window is out of play.
        FootballerObservation obs = Build(
            new Vector3(0f, 0f, pitch.HalfWidth), Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(3f, 0f, pitch.HalfWidth - 2f)) }, tick: 10, pitch: pitch);

        Assert.True(field.TryBuild(obs, new Vector2(0f, pitch.HalfWidth), pitch));

        int boundary = 0;
        int inside = 0;
        foreach (PressureCell cell in field.Cells)
        {
            if (cell.IsBoundaryCell)
            {
                boundary++;
                Assert.Equal(0f, cell.Pressure01);
                Assert.Equal(0f, cell.InfluenceShare);
                Assert.Equal(1f, cell.UnclaimedShare);
                Assert.True(cell.IsUnclaimed);
                Assert.Equal(-1f, cell.MyArrivalSeconds);
                Assert.Equal(-1f, cell.OpponentArrivalSeconds);
            }
            else
            {
                inside++;
            }
        }

        Assert.True(boundary > 0, "no cell was flagged as out of play");
        Assert.True(inside > 0, "the whole window was treated as out of play");
    }

    [Fact]
    public void ArrivalMargin_IsPositiveWhenThePlayerIsThereFirst()
    {
        var field = new LocalPressureField();
        // An opponent two metres behind a point four metres in front of me: I must win the race.
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(-2f, 0f, 0f)) }, tick: 10);

        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));

        PressureCell ahead = field.Read(new Vector2(3f, 0f), out bool inside);
        Assert.True(inside);
        Assert.True(ahead.MyArrivalSeconds >= 0f);
        Assert.True(ahead.OpponentArrivalSeconds >= 0f);
        Assert.True(ahead.ArrivalMarginSeconds > 0f,
            $"margin {ahead.ArrivalMarginSeconds:F3}s (mine {ahead.MyArrivalSeconds:F3}, theirs {ahead.OpponentArrivalSeconds:F3})");
        Assert.True(ahead.InfluenceShare > ahead.OpponentInfluenceShare,
            $"influence {ahead.InfluenceShare:F3} vs {ahead.OpponentInfluenceShare:F3}");

        // The point behind me is the opponent's race, and the share must flip.
        PressureCell behind = field.Read(new Vector2(-3f, 0f), out _);
        Assert.True(behind.ArrivalMarginSeconds < 0f,
            $"margin {behind.ArrivalMarginSeconds:F3}s (mine {behind.MyArrivalSeconds:F3}, theirs {behind.OpponentArrivalSeconds:F3})");
        Assert.True(behind.InfluenceShare < behind.OpponentInfluenceShare,
            $"influence {behind.InfluenceShare:F3} vs {behind.OpponentInfluenceShare:F3}");
    }

    [Fact]
    public void Pressure_IsDirectional_AnOpponentRunningAtAPointThreatensItMore()
    {
        var closing = new LocalPressureField();
        var drifting = new LocalPressureField();

        // Same position, opposite velocity: one is running at the point ahead of me, one is running away.
        FootballerObservation toward = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(7f, 0f, 0f), new Vector3(-6f, 0f, 0f)) }, tick: 10);
        FootballerObservation away = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(7f, 0f, 0f), new Vector3(6f, 0f, 0f)) }, tick: 20);

        Assert.True(closing.TryBuild(toward, new Vector2(0f, 0f)));
        Assert.True(drifting.TryBuild(away, new Vector2(0f, 0f)));

        float towardPressure = closing.PressureAt(new Vector2(3f, 0f));
        float awayPressure = drifting.PressureAt(new Vector2(3f, 0f));

        Assert.True(towardPressure > awayPressure,
            $"closing {towardPressure:F3} vs drifting {awayPressure:F3}");
        Assert.True(towardPressure > 0.2f, $"pressure {towardPressure:F3} is too low to mean anything");
    }

    [Fact]
    public void ACellNobodyCanReach_IsFlaggedUnclaimed()
    {
        var field = new LocalPressureField();
        // Alone, nearly exhausted, standing still: the far corner of the 15 m window is ~8.5 m away,
        // which this body cannot cover inside the 1.5 s horizon.
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f), Array.Empty<FootballerState>(), tick: 10,
            selfStamina: 0.15f);

        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));

        PressureCell corner = field.Read(
            new Vector2(LocalPressureField.GridRadius * LocalPressureField.CellSizeMeters,
                        LocalPressureField.GridRadius * LocalPressureField.CellSizeMeters), out bool inside);

        Assert.True(inside);
        Assert.True(corner.IsUnclaimed, "the far corner was claimed by nobody but flagged claimed");
        Assert.Equal(-1f, corner.MyArrivalSeconds);
        Assert.Equal(-1f, corner.OpponentArrivalSeconds);
    }

    [Fact]
    public void TheBuild_IsCachedForTheTick_AndRebuiltForANewOne()
    {
        var field = new LocalPressureField();
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(5f, 0f, 0f)) }, tick: 30);

        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));
        Assert.Equal(1, field.BuildCount);

        // Same observer, same tick, same centre: a decision scoring twenty candidates pays once.
        Assert.False(field.TryBuild(obs, new Vector2(0f, 0f)));
        Assert.Equal(1, field.BuildCount);
        Assert.Equal(30, field.BuiltForTick);

        // Next tick: the world moved.
        FootballerObservation next = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(5.5f, 0f, 0f)) }, tick: 31);
        Assert.True(field.TryBuild(next, new Vector2(0f, 0f)));
        Assert.Equal(2, field.BuildCount);
    }

    [Fact]
    public void AnUnknownTick_IsNeverCached()
    {
        // A standalone controller has no match clock (tick 0). Caching on it would serve a stale field to
        // the next decision with nothing to invalidate it.
        var field = new LocalPressureField();
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(5f, 0f, 0f)) }, tick: 0);

        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));
        Assert.True(field.TryBuild(obs, new Vector2(0f, 0f)));
        Assert.Equal(2, field.BuildCount);
    }

    [Fact]
    public void AContestBetweenTwoDistantPlayers_LeavesMostOfTheCellUnclaimed()
    {
        // A2.5's whole point: "even influence" between two players who are both 8 m away is not the
        // same statement as a share between two players standing on the ball. The absolute arrival times
        // stay, and the part of the cell nobody can claim yet is an explicit share.
        var field = new LocalPressureField();
        FootballerObservation obs = Build(
            Vector3.Zero, Vector3.Zero, new Vector2(1f, 0f),
            new[] { Opponent(2, new Vector3(16f, 0f, 0f)) }, tick: 10,
            selfStamina: 0.3f);

        Assert.True(field.TryBuild(obs, new Vector2(8f, 0f)));
        PressureCell cell = field.Read(new Vector2(8f, 0f), out _);

        Assert.True(cell.CanArriveMyself && cell.CanOpponentArrive);
        Assert.True(cell.UnclaimedShare > 0.2f,
            $"unclaimed {cell.UnclaimedShare:F3} on a cell both sides need seconds to reach");
        Assert.Equal(1f, cell.InfluenceShare + cell.OpponentInfluenceShare + cell.UnclaimedShare, precision: 3);
        Assert.False(cell.IsUnclaimed);

        // The same field's centre, where the player already is, is nearly all his.
        PressureCell here = field.Read(new Vector2(0f, 0f), out _);
        Assert.True(here.InfluenceShare > cell.InfluenceShare,
            $"centre {here.InfluenceShare:F3} vs contested {cell.InfluenceShare:F3}");
        Assert.True(here.UnclaimedShare < cell.UnclaimedShare,
            $"centre unclaimed {here.UnclaimedShare:F3} vs contested {cell.UnclaimedShare:F3}");
    }

    [Fact]
    public void TheArrivalKernel_RespondsToDistanceVelocityAndFatigue()
    {
        ReachKinematics resting = ReachKinematics.Neutral(Vector2.Zero, Vector2.Zero);
        const float LongHorizon = 10f;

        float near = LocalPressureField.EstimateArrivalSeconds(resting, new Vector2(4f, 0f), LongHorizon);
        float far = LocalPressureField.EstimateArrivalSeconds(resting, new Vector2(9f, 0f), LongHorizon);
        Assert.True(near >= 0f && far > near, $"near {near:F3}s, far {far:F3}s");

        ReachKinematics running = resting with { VelocityXZ = new Vector2(7f, 0f) };
        Assert.True(LocalPressureField.EstimateArrivalSeconds(running, new Vector2(9f, 0f), LongHorizon) < far,
            "already running at the point did not arrive earlier");

        ReachKinematics tired = resting with { Stamina01 = 0.3f };
        Assert.True(LocalPressureField.EstimateArrivalSeconds(tired, new Vector2(9f, 0f), LongHorizon) > far,
            "fatigue did not slow the arrival");

        // Beyond the horizon the answer is "no", not "very late".
        // Beyond the default horizon the answer is "no", not "very late".
        Assert.Equal(-1f, LocalPressureField.EstimateArrivalSeconds(resting, new Vector2(40f, 0f)));
    }

    [Fact]
    public void TheKernel_AgreesWithTheReachModelOnStraightLineRuns()
    {
        // The kernel drops the turn term on purpose; everything else must match, or a scorer that reads
        // a cell and a scorer that reads the reach model would disagree about the same player.
        var kinematics = ReachKinematics.Neutral(Vector2.Zero, Vector2.Zero);
        for (float distance = 3f; distance <= 12f; distance += 3f)
        {
            var target = new Vector2(distance, 0f);
            float kernel = LocalPressureField.EstimateArrivalSeconds(kinematics, target, horizon: 10f);
            float model = TimeToReachModel.ToPoint(kinematics, target, horizonSeconds: 10f).Seconds;

            Assert.InRange(kernel, model * 0.85f, model * 1.15f);
        }
    }
}
