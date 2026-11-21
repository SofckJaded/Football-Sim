using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A8.2 — the shallow continuation search, on its own.
///
/// The search is the part of A8 that decides <i>which</i> continuation is worth a contract; these tests pin
/// the properties the plan and the exit gate ask of it: at most three first actions with at most three
/// continuations each, a continuation chosen from the carrier's own picture (never a truth rollout), the
/// defenders' sighting uncertainty charged against the promise, a blocked return refused cleanly, and
/// tick-for-tick determinism. The integration with the carrier's one decision pipeline is covered by
/// <see cref="CombinationScenarioTests"/>.
/// </summary>
public sealed class ShallowActionSearchTests
{
    private const int SelfId = 8;
    private const int ReceiverId = 9;
    private const int RunnerId = 10;

    private static readonly PitchDimensions Pitch = new();
    private static readonly PassPlanner Planner = new(Pitch);
    private static readonly PassSuccessModel Success = new(Planner);

    // ---------------------------------------------------------------------------------------------
    // The move the search exists to find.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AFreeOneTwo_IsProposed_WithTheRegionAheadOfTheInitiator()
    {
        ShallowActionSearch search = NewSearch();

        search.Search(
            Observation(100, Mate(ReceiverId, new Vector2(9f, 4f))),
            new[] { ReceiverId },
            BallXZ,
            offsideLineProjX: 30f,
            runs: null);

        Assert.Equal(1, search.ProposalCount);
        Assert.Equal(1, search.FirstActionsEvaluated);
        Assert.True(search.TryGetBestProposal(out ShallowContinuation best));
        Assert.Equal(CombinationKind.OneTwo, best.Kind);
        Assert.Equal(ReceiverId, best.ReceiverId);
        Assert.Equal(0, best.ThirdPlayerId);

        // Deep return region: eight metres ahead of the initiator, offset toward the receiver's side.
        var expected = ShallowActionSearch.OneTwoRegion(
            Vector2.Zero, new Vector2(9f, 4f), attackingDirectionX: 1f,
            CandidateGenerator.OneTwoReturnForwardMeters, CandidateGenerator.OneTwoReturnLateralMeters);
        Assert.Equal(expected.X, best.RegionXZ.X, 3);
        Assert.Equal(expected.Y, best.RegionXZ.Y, 3);
        Assert.InRange(best.RegionRadiusMeters, 0.5f, 10f);
        Assert.InRange(best.ArrivalTick, 101, 100 + CombinationCoordinator.MaxArrivalLeadTicks);
        Assert.InRange(best.Confidence01, 0.01f, 1f);
        Assert.True(best.Value01 >= ShallowActionSearch.MinContinuationValue01);
        Assert.InRange(best.FirstLegCompletion01, 0.01f, 1f);
    }

    [Fact]
    public void TheOneTwoRegion_IsPlacedTowardTheReceiversSide()
    {
        Vector2 region = ShallowActionSearch.OneTwoRegion(
            new Vector2(0f, 0f), new Vector2(6f, -8f), attackingDirectionX: 1f, 8f, 2.5f);

        Assert.Equal(8f, region.X, 3);
        Assert.Equal(-2.5f, region.Y, 3);
    }

    // ---------------------------------------------------------------------------------------------
    // The plan's budget: three first actions, three continuations each, deterministic.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSearch_ExaminesAtMostThreeFirstActionsAndThreeContinuationsEach()
    {
        var visible = new List<PerceivedEntity>
        {
            Mate(ReceiverId + 0, new Vector2(8f, 4f)),
            Mate(ReceiverId + 1, new Vector2(6f, -8f)),
            Mate(ReceiverId + 2, new Vector2(12f, 1f)),
            Mate(ReceiverId + 3, new Vector2(10f, 12f)),
            Mate(ReceiverId + 4, new Vector2(4f, 14f)),
            Mate(ReceiverId + 5, new Vector2(16f, -5f))
        };
        var ranked = new int[visible.Count];
        for (int i = 0; i < visible.Count; i++) ranked[i] = ReceiverId + i;

        ShallowActionSearch search = NewSearch();
        search.Search(Observation(100, visible.ToArray()), ranked, BallXZ, 40f, runs: null);

        Assert.Equal(ShallowActionSearch.MaxFirstActions, search.FirstActionsEvaluated);
        Assert.InRange(search.ContinuationsEvaluated, 1, ShallowActionSearch.MaxContinuationsEvaluated);
        Assert.InRange(search.ProposalCount, 1, ShallowActionSearch.MaxFirstActions);
    }

    [Fact]
    public void TheSearch_IsDeterministic()
    {
        PerceivedEntity[] visible =
        {
            Mate(ReceiverId, new Vector2(9f, 4f)),
            Mate(ReceiverId + 1, new Vector2(5f, -7f)),
            Opponent(201, new Vector2(7f, 1f)),
            Opponent(202, new Vector2(14f, -3f))
        };

        ShallowActionSearch first = NewSearch();
        ShallowActionSearch second = NewSearch();
        first.Search(Observation(100, visible), new[] { ReceiverId, ReceiverId + 1 }, BallXZ, 30f, null);
        second.Search(Observation(100, visible), new[] { ReceiverId, ReceiverId + 1 }, BallXZ, 30f, null);

        Assert.Equal(first.FirstActionsEvaluated, second.FirstActionsEvaluated);
        Assert.Equal(first.ContinuationsEvaluated, second.ContinuationsEvaluated);
        Assert.Equal(first.ProposalCount, second.ProposalCount);
        for (int i = 0; i < first.ProposalCount; i++)
        {
            Assert.Equal(first.ProposalAt(i).Kind, second.ProposalAt(i).Kind);
            Assert.Equal(first.ProposalAt(i).ReceiverId, second.ProposalAt(i).ReceiverId);
            Assert.Equal(first.ProposalAt(i).RegionXZ, second.ProposalAt(i).RegionXZ);
            Assert.Equal(first.ProposalAt(i).ArrivalTick, second.ProposalAt(i).ArrivalTick);
            Assert.Equal(first.ProposalAt(i).Confidence01, second.ProposalAt(i).Confidence01);
            Assert.Equal(first.ProposalAt(i).Value01, second.ProposalAt(i).Value01);
        }
    }

    [Fact]
    public void AReceiverTheCarrierCannotSee_IsNotExamined()
    {
        ShallowActionSearch search = NewSearch();

        // The ranked list names a receiver, but the observation does not contain him: the search has no
        // other source of positions, so he cannot be examined.
        search.Search(Observation(100), new[] { ReceiverId }, BallXZ, 30f, runs: null);

        Assert.Equal(0, search.FirstActionsEvaluated);
        Assert.Equal(0, search.ProposalCount);
    }

    // ---------------------------------------------------------------------------------------------
    // A blocked return aborts cleanly: the promise is not made at all.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ABlockedDeepReturn_FallsBackToTheShortBounce_AndABlockedBounceMakesNoProposal()
    {
        // The receiver is upfield of the initiator and a defender stands on the deep return line, so the
        // deep region is not a lane but the short bounce is.
        var blockedDeep = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector2(4f, 10f)),
            Opponent(201, new Vector2(7.2f, 4f))
        };

        ShallowActionSearch search = NewSearch();
        search.Search(Observation(100, blockedDeep.ToArray()), new[] { ReceiverId }, BallXZ, 30f, null);

        Assert.Equal(1, search.ProposalCount);
        Assert.True(search.TryGetBestProposal(out ShallowContinuation best));
        Assert.Equal(CombinationKind.OneTwo, best.Kind);

        var deep = ShallowActionSearch.OneTwoRegion(
            Vector2.Zero, new Vector2(4f, 10f), 1f,
            CandidateGenerator.OneTwoReturnForwardMeters, CandidateGenerator.OneTwoReturnLateralMeters);
        Assert.True(best.RegionXZ.X < deep.X - 0.5f,
            $"a blocked deep return must not be proposed (region {best.RegionXZ.Describe()}, deep {deep.Describe()})");

        // A line and a defender that remove every return: no promise at all, so no contract can be made.
        var allBlocked = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector2(4f, 10f)),
            Opponent(201, new Vector2(7.2f, 4f)),
            Opponent(202, new Vector2(4.9f, 5f))
        };
        ShallowActionSearch blocked = NewSearch();
        blocked.Search(Observation(100, allBlocked.ToArray()), new[] { ReceiverId }, BallXZ, 30f, null);
        Assert.Equal(0, blocked.ProposalCount);
    }

    [Fact]
    public void AnOffsideLineBelowEveryReturnRegion_MakesNoProposal()
    {
        ShallowActionSearch search = NewSearch();

        // Max attacking projection is 4 - 1 = 3 m: neither the deep (8 m) nor the short (5 m) region is
        // onside, so the initiator could never legally run into it.
        search.Search(
            Observation(100, Mate(ReceiverId, new Vector2(9f, 4f))),
            new[] { ReceiverId },
            BallXZ,
            offsideLineProjX: 4f,
            runs: null);

        Assert.Equal(0, search.ProposalCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Defender uncertainty: a stale sighting is charged against the promise.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AStaleSightingDegradesThePromise_WhereTheSameFreshPositionDoesNot()
    {
        const float uncertaintyMeters = 2.0f;
        var fresh = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector2(4f, 10f)),
            Opponent(201, new Vector2(7.76f, 7.19f))
        };
        var stale = new List<PerceivedEntity>
        {
            Mate(ReceiverId, new Vector2(4f, 10f)),
            Opponent(201, new Vector2(7.76f, 7.19f), uncertaintyMeters)
        };

        ShallowActionSearch freshSearch = NewSearch();
        freshSearch.Search(Observation(100, fresh.ToArray()), new[] { ReceiverId }, BallXZ, 30f, null);
        ShallowActionSearch staleSearch = NewSearch();
        staleSearch.Search(Observation(100, stale.ToArray()), new[] { ReceiverId }, BallXZ, 30f, null);

        Assert.Equal(1, freshSearch.ProposalCount);
        Assert.True(freshSearch.TryGetBestProposal(out ShallowContinuation freshProposal));

        Assert.True(staleSearch.ProposalCount >= 1);
        Assert.True(staleSearch.TryGetBestProposal(out ShallowContinuation staleProposal));

        // Same believed positions; only the sighting's age differs. The deep promise the fresh picture
        // allows is not made from the stale one — the search falls back to the shorter bounce, which the
        // widened corridor still clears.
        Assert.True(freshProposal.RegionXZ.X > staleProposal.RegionXZ.X + 0.5f,
            $"stale {staleProposal.Describe()} should not keep the deep region the fresh {freshProposal.Describe()} offers");
        Assert.True(staleProposal.Confidence01 < freshProposal.Confidence01);
    }

    // ---------------------------------------------------------------------------------------------
    // The third-player branch: an advertised run is the only way a layoff can exist.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AnAdvertisedRun_CanOutrankTheOneTwo_AndNeedsATeammateWorthLayingOffTo()
    {
        var board = new RunAdvertisementBoard();
        PublishRun(board, RunnerId, startXZ: new Vector2(14f, 2f), meetXZ: new Vector2(18f, 6f),
            publishedTick: 80, meetTick: 200, expiresTick: 290);

        PerceivedEntity[] visible =
        {
            Mate(ReceiverId, new Vector2(6f, 0f)),
            Mate(RunnerId, new Vector2(14f, 2f))
        };

        ShallowActionSearch search = NewSearch();
        search.Search(Observation(100, visible), new[] { ReceiverId }, BallXZ, 40f, board);

        Assert.Equal(1, search.ProposalCount);
        Assert.True(search.TryGetBestProposal(out ShallowContinuation best));
        Assert.Equal(CombinationKind.ThirdPlayer, best.Kind);
        Assert.Equal(ReceiverId, best.ReceiverId);
        Assert.Equal(RunnerId, best.ThirdPlayerId);
        Assert.Equal(new Vector2(18f, 6f), best.RegionXZ);
    }

    [Fact]
    public void AnAdvertisementTheTeamCannotHearYet_ProducesNoThirdPlayerProposal()
    {
        var board = new RunAdvertisementBoard();
        // Published on the observation's own tick: the board's communication delay hides it for 18 ticks.
        PublishRun(board, RunnerId, startXZ: new Vector2(14f, 2f), meetXZ: new Vector2(18f, 6f),
            publishedTick: 100, meetTick: 200, expiresTick: 290);

        PerceivedEntity[] visible =
        {
            Mate(ReceiverId, new Vector2(6f, 0f)),
            Mate(RunnerId, new Vector2(14f, 2f))
        };

        ShallowActionSearch search = NewSearch();
        search.Search(Observation(100, visible), new[] { ReceiverId }, BallXZ, 40f, board);

        Assert.True(search.ProposalCount >= 1);
        for (int i = 0; i < search.ProposalCount; i++)
            Assert.NotEqual(CombinationKind.ThirdPlayer, search.ProposalAt(i).Kind);
    }

    // ---------------------------------------------------------------------------------------------
    // The first leg is part of the promise: a contested first ball lowers the confidence.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AContestedFirstLeg_LowersTheConfidenceOfTheSameContinuation()
    {
        PerceivedEntity[] free =
        {
            Mate(ReceiverId, new Vector2(9f, 4f))
        };
        PerceivedEntity[] contested =
        {
            Mate(ReceiverId, new Vector2(9f, 4f)),
            // Two metres beyond the receiver, on the far side from the return region: the first pass is
            // priced down by a defender who can be on it, while the receiver-to-region lane stays clear.
            Opponent(201, new Vector2(10.1f, 5.7f))
        };

        ShallowActionSearch freeSearch = NewSearch();
        freeSearch.Search(Observation(100, free), new[] { ReceiverId }, BallXZ, 30f, null);
        ShallowActionSearch contestedSearch = NewSearch();
        contestedSearch.Search(Observation(100, contested), new[] { ReceiverId }, BallXZ, 30f, null);

        Assert.True(freeSearch.ProposalCount == 1,
            $"free: proposals {freeSearch.ProposalCount}, first actions {freeSearch.FirstActionsEvaluated}, " +
            $"continuations {freeSearch.ContinuationsEvaluated}");
        Assert.True(contestedSearch.ProposalCount == 1,
            $"contested: proposals {contestedSearch.ProposalCount}, first actions {contestedSearch.FirstActionsEvaluated}, " +
            $"continuations {contestedSearch.ContinuationsEvaluated}");
        Assert.True(freeSearch.TryGetBestProposal(out ShallowContinuation freeProposal));
        Assert.True(contestedSearch.TryGetBestProposal(out ShallowContinuation contestedProposal));

        Assert.Equal(freeProposal.Kind, contestedProposal.Kind);
        Assert.True(contestedProposal.FirstLegCompletion01 < freeProposal.FirstLegCompletion01);
        Assert.True(contestedProposal.Confidence01 < freeProposal.Confidence01,
            $"contested first leg: {contestedProposal.Describe()}; free: {freeProposal.Describe()}");
    }

    // ---------------------------------------------------------------------------------------------
    // The switch ships on, and its Describe line names it.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultConfiguration_ShipsTheSearch()
    {
        Assert.True(new AiRuntimeConfig().EnableShallowSearch);
        Assert.Contains("search(on)", new AiRuntimeConfig().Describe());
        Assert.Contains("search(off)",
            new AiRuntimeConfig { EnableShallowSearch = false }.Describe());
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers: observations in the carrier's own terms and one advertised run.
    // ---------------------------------------------------------------------------------------------

    private static readonly Vector2 BallXZ = new(0.4f, 0f);

    private static ShallowActionSearch NewSearch() => new(Pitch, Planner, Success);

    private static PerceivedEntity Mate(int id, Vector2 position, Vector2 velocity = default) => new(
        id,
        TeamId.Home,
        TacticalRole.CentralMidfielder,
        new Vector3(position.X, 0f, position.Y),
        new Vector3(velocity.X, 0f, velocity.Y),
        1f);

    private static PerceivedEntity Opponent(int id, Vector2 position, float uncertaintyMeters = 0f) => new(
        id,
        TeamId.Away,
        TacticalRole.CenterBack,
        new Vector3(position.X, 0f, position.Y),
        Vector3.Zero,
        1f)
    {
        PositionUncertaintyMeters = uncertaintyMeters
    };

    private static FootballerObservation Observation(int tick, params PerceivedEntity[] visible) => new()
    {
        SelfId = SelfId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = Vector3.Zero,
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(BallXZ.X, 0f, BallXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = SelfId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = SelfId,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = new PlayerTendencies(),
        SelfAttributes = new PlayerAttributes()
    };

    private static void PublishRun(
        RunAdvertisementBoard board,
        int runnerId,
        Vector2 startXZ,
        Vector2 meetXZ,
        int publishedTick,
        int meetTick,
        int expiresTick)
    {
        var path = new RunPath(
            RunKind.InBehind,
            StartXZ: startXZ,
            MeetXZ: meetXZ,
            MeetRadiusMeters: 3f,
            StartTick: publishedTick,
            MeetTick: meetTick,
            ExpiresTick: expiresTick,
            HoldUntilTick: publishedTick,
            HoldXZ: startXZ,
            Confidence01: 0.9f);
        board.Publish(TeamId.Home, runnerId, new RunIntent(path, TacticalJob.DepthRun, publishedTick),
            publishedTick);
    }
}

/// <summary>Compact vector text for a failing assertion: "x,y".</summary>
internal static class ShallowSearchTestExtensions
{
    public static string Describe(this Vector2 value) => $"({value.X:F2},{value.Y:F2})";
}
