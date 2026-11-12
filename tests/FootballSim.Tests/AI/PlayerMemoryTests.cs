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
/// A2.2 — sight filtering and persistent observation memory.
///
/// The hard invariant is negative and therefore the most important test here: an opponent the player
/// cannot see must not be refreshable from the authoritative world. Before A2.2 every active opponent
/// was in every observation on every tick, so an unseen player's live motion reached every scorer and
/// no "confidence" number could change that. These tests move a hidden opponent, change its direction,
/// and assert that nothing in the observer's picture of the pitch (or in a candidate score) notices.
/// </summary>
public sealed class PlayerMemoryTests
{
    private const float Dt = SimConstants.FixedDeltaTime;
    private const int Self = 1;
    private const int Opponent = 2;

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

    private static FootballerObservation Observe(
        PerceptionSystem perception,
        FootballerState self,
        IReadOnlyList<FootballerState> players,
        BallState ball,
        SeededRandom rng,
        int tick) =>
        perception.BuildObservation(
            self, players, ball, null, MatchPhase.FirstHalf, tick * Dt, attackingDirX: 1f,
            isSetPieceTaker: false, TeamTacticalProfile.PossessionControl, rng, tick: tick);

    private static BallState FarBall() => new() { Position = new Vector3(40f, 0.11f, -20f) };

    private static PerceivedEntity Find(in FootballerObservation observation, int playerId)
    {
        foreach (PerceivedEntity entity in observation.VisiblePlayers)
            if (entity.PlayerId == playerId) return entity;
        throw new InvalidOperationException($"Player #{playerId} is not in the observation.");
    }

    // ---- the boundary ---------------------------------------------------------------------------

    [Fact]
    public void AnUnseenOpponent_KeepsTheLastSighting_NotTheLivePosition()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(77);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState opponent = Player(Opponent, TeamId.Away, TacticalRole.Striker, new Vector3(10f, 0f, 0f), new Vector2(-1f, 0f));
        var ball = FarBall();

        FootballerObservation first = Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 1);
        Vector3 seenAt = Find(first, Opponent).EstimatedPosition;

        // Now the opponent is behind the observer and sprinting away, hard. Neither the position nor
        // the velocity of the observer's picture may follow.
        opponent.Position = new Vector3(-14f, 0f, 0f);
        opponent.Velocity = new Vector3(-8.4f, 0f, 0f);

        FootballerObservation second = Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 2);
        PerceivedEntity remembered = Find(second, Opponent);

        Assert.True(remembered.ObservedAgeTicks > 0, "the unseen opponent was published as freshly observed");
        Assert.True(Vector3.Distance(remembered.EstimatedPosition, opponent.Position) > 10f,
            $"the estimate followed the live position: estimate {remembered.EstimatedPosition}, live {opponent.Position}");
        Assert.True(remembered.EstimatedVelocity.Length() < 0.5f,
            $"the estimate took the live velocity: {remembered.EstimatedVelocity}");

        // The belief itself is the stored sighting, not a live view.
        PlayerBelief? belief = perception.GetBelief(Self, Opponent);
        Assert.NotNull(belief);
        Assert.Equal(1, belief!.LastObservedTick);
        Assert.Equal(seenAt, belief.Position);
    }

    [Fact]
    public void AnUnseenOpponent_ChangingDirection_CannotChangeThePicture()
    {
        // Two identical worlds except that the hidden opponent runs the other way: the observation the
        // observer builds must be identical. This is the leak the A2 exit gate names.
        (FootballerObservation Left, FootballerObservation Right) = HiddenOpponentPair(Vector3.UnitZ * 7f, -Vector3.UnitZ * 7f);

        Assert.Equal(Left.VisiblePlayers.Count, Right.VisiblePlayers.Count);
        for (int i = 0; i < Left.VisiblePlayers.Count; i++)
        {
            PerceivedEntity a = Left.VisiblePlayers[i];
            PerceivedEntity b = Right.VisiblePlayers[i];
            Assert.Equal(a.PlayerId, b.PlayerId);
            Assert.Equal(a.EstimatedPosition, b.EstimatedPosition);
            Assert.Equal(a.EstimatedVelocity, b.EstimatedVelocity);
            Assert.Equal(a.ObservedAgeTicks, b.ObservedAgeTicks);
            Assert.Equal(a.PositionUncertaintyMeters, b.PositionUncertaintyMeters);
            Assert.Equal(a.Confidence01, b.Confidence01);
        }

        Assert.Equal(Left.KnowledgeAgeTicks, Right.KnowledgeAgeTicks);
    }

    [Fact]
    public void AnUnseenOpponent_ChangingDirection_CannotChangeACandidateScore()
    {
        // The decision side of the same invariant: feed the two identical pictures to a controller and
        // the candidate table must match term for term.
        (FootballerObservation Left, FootballerObservation Right) = HiddenOpponentPair(Vector3.UnitZ * 7f, -Vector3.UnitZ * 7f);
        var pitch = new PitchDimensions();

        var leftAi = new TacticalUtilityAiController(pitch);
        var rightAi = new TacticalUtilityAiController(pitch);
        leftAi.Decide(Left, Dt);
        rightAi.Decide(Right, Dt);

        Assert.Equal(leftAi.LastCandidateCount, rightAi.LastCandidateCount);
        Assert.Equal(leftAi.LastSelected.Identity, rightAi.LastSelected.Identity);
        Assert.Equal(leftAi.LastSelected.UtilityScore, rightAi.LastSelected.UtilityScore);

        // A candidate set that was empty on both sides would pass this trivially.
        Assert.True(leftAi.LastCandidateCount > 0);
    }

    /// <summary>
    /// Two runs that agree on everything the observer can see, and differ only in the motion of an
    /// opponent behind them.
    /// </summary>
    private static (FootballerObservation Left, FootballerObservation Right) HiddenOpponentPair(
        Vector3 leftVelocity,
        Vector3 rightVelocity)
    {
        FootballerObservation Run(Vector3 hiddenVelocity)
        {
            var perception = new PerceptionSystem();
            var rng = new SeededRandom(1234);
            FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
            FootballerState ahead = Player(3, TeamId.Home, TacticalRole.Striker, new Vector3(12f, 0f, 4f), new Vector2(1f, 0f));
            FootballerState hidden = Player(Opponent, TeamId.Away, TacticalRole.CenterBack, new Vector3(-11f, 0f, 0f), new Vector2(1f, 0f));
            var ball = FarBall();

            // First look: everybody is seen.
            Observe(perception, self, new[] { self, ahead, hidden }, ball, rng, tick: 1);

            // The hidden opponent then moves without ever coming into sight.
            hidden.Position = new Vector3(-6f, 0f, -2f);
            hidden.Velocity = hiddenVelocity;
            return Observe(perception, self, new[] { self, ahead, hidden }, ball, rng, tick: 2);
        }

        return (Run(leftVelocity), Run(rightVelocity));
    }

    // ---- ageing --------------------------------------------------------------------------------

    [Fact]
    public void AnUnseenOpponent_Ages_GrowsUncertain_AndLosesConfidence()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(9);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState opponent = Player(Opponent, TeamId.Away, TacticalRole.Striker, new Vector3(8f, 0f, 0f), new Vector2(-1f, 0f));
        var ball = FarBall();

        Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 1);
        PerceivedEntity firstSighting = Find(Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 1), Opponent);

        // Sprint away behind the observer's back, where nothing is watching.
        opponent.Position = new Vector3(-8f, 0f, 0f);
        opponent.Velocity = new Vector3(-7f, 0f, 0f);

        int previousAge = 0;
        float previousUncertainty = 0f;
        float previousConfidence = float.MaxValue;
        foreach (int tick in new[] { 5, 30, 90, 180 })
        {
            PerceivedEntity aged = Find(Observe(perception, self, new[] { self, opponent }, ball, rng, tick), Opponent);

            Assert.True(aged.ObservedAgeTicks > previousAge, $"age did not grow at t{tick}: {aged.ObservedAgeTicks}");
            Assert.True(aged.PositionUncertaintyMeters > previousUncertainty,
                $"uncertainty did not grow at t{tick}: {aged.PositionUncertaintyMeters}");
            Assert.True(aged.Confidence01 < previousConfidence,
                $"confidence did not fall at t{tick}: {aged.Confidence01}");
            Assert.True(aged.Confidence01 >= PlayerMemory.MinConfidence01);
            Assert.True(float.IsFinite(aged.EstimatedPosition.X) && float.IsFinite(aged.EstimatedPosition.Z));

            previousAge = aged.ObservedAgeTicks;
            previousUncertainty = aged.PositionUncertaintyMeters;
            previousConfidence = aged.Confidence01;
        }

        // The last sighting had the opponent at z = -8 moving nowhere, so even after 3 s the belief must
        // still be near that spot rather than tracking the sprint.
        PerceivedEntity oldest = Find(Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 180), Opponent);
        Assert.True(Vector3.Distance(oldest.EstimatedPosition, firstSighting.EstimatedPosition) < PlayerMemory.MaxExtrapolationMeters);
    }

    [Fact]
    public void ASeenOpponent_IsRefreshedEveryTick()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(31);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState opponent = Player(Opponent, TeamId.Away, TacticalRole.Striker, new Vector3(9f, 0f, 0f), new Vector2(-1f, 0f));
        var ball = FarBall();

        for (int tick = 1; tick <= 30; tick++)
        {
            opponent.Position = new Vector3(9f - tick * 0.1f, 0f, 0f);
            PerceivedEntity seen = Find(Observe(perception, self, new[] { self, opponent }, ball, rng, tick), Opponent);
            Assert.Equal(0, seen.ObservedAgeTicks);
            Assert.Equal(0f, seen.PositionUncertaintyMeters);
            Assert.True(Vector3.Distance(seen.EstimatedPosition, opponent.Position) < 1f,
                $"the estimate drifted {Vector3.Distance(seen.EstimatedPosition, opponent.Position):F2} m from a seen opponent");
        }

        PlayerBelief? belief = perception.GetBelief(Self, Opponent);
        Assert.NotNull(belief);
        Assert.Equal(30, belief!.LastObservedTick);
        Assert.Equal(0, belief.AgeTicks(30));
    }

    [Fact]
    public void TheFirstLook_SeesEveryone_SoAFragmentDoesNotStartBlind()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(5);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState behind = Player(Opponent, TeamId.Away, TacticalRole.Striker, new Vector3(-12f, 0f, 0f), new Vector2(-1f, 0f));
        var ball = FarBall();

        FootballerObservation first = Observe(perception, self, new[] { self, behind }, ball, rng, tick: 1);
        Assert.Equal(0, Find(first, Opponent).ObservedAgeTicks);

        // From the second tick on, sight governs.
        FootballerObservation second = Observe(perception, self, new[] { self, behind }, ball, rng, tick: 2);
        Assert.Equal(1, Find(second, Opponent).ObservedAgeTicks);
    }

    [Fact]
    public void TheMemoryIsBoundedByTheNumberOfPlayers()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(3);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        var ball = FarBall();
        var others = new List<FootballerState> { self };
        for (int id = 10; id < 20; id++)
            others.Add(Player(id, TeamId.Away, TacticalRole.CenterBack, new Vector3(id, 0f, -10f), new Vector2(-1f, 0f)));

        for (int tick = 1; tick <= 120; tick++)
            Observe(perception, self, others, ball, rng, tick);

        // Only the players that have ever been seen, never one entry per tick.
        Assert.Equal(others.Count - 1, perception.GetMemory(Self).BeliefCount);
    }

    [Fact]
    public void Forget_RemovesTheBeliefEverywhere_AndClear_ResetsTheMemory()
    {
        var perception = new PerceptionSystem();
        var rng = new SeededRandom(11);
        FootballerState self = Player(Self, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, new Vector2(1f, 0f));
        FootballerState opponent = Player(Opponent, TeamId.Away, TacticalRole.Striker, new Vector3(8f, 0f, 0f), new Vector2(-1f, 0f));
        var ball = FarBall();

        Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 1);
        Assert.NotNull(perception.GetBelief(Self, Opponent));

        perception.Forget(Opponent);
        Assert.Null(perception.GetBelief(Self, Opponent));
        Assert.Null(perception.GetBelief(Opponent, Self));

        // A forgotten player who is not visible is simply absent from the picture, not a guess at origin.
        opponent.Position = new Vector3(-14f, 0f, 0f);
        FootballerObservation afterForget = Observe(perception, self, new[] { self, opponent }, ball, rng, tick: 2);
        Assert.DoesNotContain(afterForget.VisiblePlayers, e => e.PlayerId == Opponent);

        perception.Clear();
        Assert.Equal(0, perception.GetMemory(Self).BeliefCount);
        Assert.False(perception.GetMemory(Self).IsInitialised);
    }
}
