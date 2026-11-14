using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.5 — complementary box arrivals.
///
/// Two forwards attacking the same post at the same moment create the congestion they were trying to
/// exploit; the one who arrives later or wider complements the other. The runner learns about his
/// team-mate's arrival the only way the simulation allows — from the A5.3 board, a delayed statement by the
/// other runner — so the term is a team statement rather than a second reading of the world. It also
/// carries the player's own finishing/aerial capability, because an arrival is only worth what the man can
/// do with the ball. The term is signed around zero and applies only to box kinds: it can reorder arrivals,
/// never shift the rest of the run set.
/// </summary>
public sealed class BoxArrivalTests
{
    private static readonly PitchDimensions Pitch = new();
    private static readonly RestDefence Secure = new(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f);

    // ---- the term itself --------------------------------------------------------------------------

    [Fact]
    public void ABoxArrivalThatDuplicatesAnAnnouncedOne_IsWorthLess()
    {
        RunPath arrival = BoxPath(new Vector2(40f, 6f), meetTick: 460);
        FootballerObservation obs = BoxObservation();

        float alone = RunScoring.Score(arrival, obs, CarrierXZ, Secure, TeamBoxArrivals.None).Score;
        float crowded = RunScoring.Score(arrival, obs, CarrierXZ, Secure,
            Arrivals((new Vector2(40f, 6f), 460))).Score;

        Assert.True(crowded < alone, $"a duplicate arrival must be worth less ({crowded:F3} vs {alone:F3})");
    }

    [Fact]
    public void AnArrivalAtAnotherPostOrAnotherTime_IsComplementary()
    {
        RunPath arrival = BoxPath(new Vector2(40f, 6f), meetTick: 460);
        FootballerObservation obs = BoxObservation();
        float alone = RunScoring.Score(arrival, obs, CarrierXZ, Secure, TeamBoxArrivals.None).Score;

        float otherPost = RunScoring.Score(arrival, obs, CarrierXZ, Secure,
            Arrivals((new Vector2(40f, -14f), 460))).Score;
        float otherTime = RunScoring.Score(arrival, obs, CarrierXZ, Secure,
            Arrivals((new Vector2(40f, 6f), 560))).Score;

        Assert.Equal(alone, otherPost, 4);
        Assert.Equal(alone, otherTime, 4);
    }

    [Fact]
    public void ABetterFinisher_RatesABoxArrivalHigher()
    {
        RunPath arrival = BoxPath(new Vector2(40f, 6f), meetTick: 460);
        FootballerObservation sharp = BoxObservation(composure: 0.95f, strength: 0.85f);
        FootballerObservation blunt = BoxObservation(composure: 0.30f, strength: 0.35f);

        float sharpScore = RunScoring.Score(arrival, sharp, CarrierXZ, Secure).Score;
        float bluntScore = RunScoring.Score(arrival, blunt, CarrierXZ, Secure).Score;

        Assert.True(sharpScore > bluntScore,
            $"a better finisher's arrival must be worth more ({sharpScore:F3} vs {bluntScore:F3})");
        Assert.True(RunScoring.BoxArrivalCapability(sharp.SelfAttributes) >
                    RunScoring.BoxArrivalCapability(blunt.SelfAttributes));
    }

    [Fact]
    public void ANonBoxPath_IsNotAffectedByTeamArrivals()
    {
        RunPath checkShort = BoxPath(new Vector2(30f, 14f), meetTick: 460) with { Kind = RunKind.CheckShort };
        FootballerObservation obs = BoxObservation();

        float alone = RunScoring.Score(checkShort, obs, CarrierXZ, Secure, TeamBoxArrivals.None).Score;
        float crowded = RunScoring.Score(checkShort, obs, CarrierXZ, Secure,
            Arrivals((new Vector2(30f, 14f), 460))).Score;

        Assert.Equal(alone, crowded, 4);
        Assert.Equal(0f, RunScoring.Score(checkShort, obs, CarrierXZ, Secure).ArrivalFit01, 4);
    }

    // ---- the board is what the runner hears ------------------------------------------------------

    [Fact]
    public void TheTeamsAdvertisedArrival_LowersThisRunnersBoxArrival()
    {
        // First generation with no board: learn the region and time of the box arrival this runner offers.
        var alone = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = BoxObservation();
        Assert.True(alone.Generate(obs, 46f, obs.Tick, 4) > 0);
        RunPath mine = PathOfKind(alone, RunKind.LateBoxArrival);
        float aloneFit = FitOf(alone, RunKind.LateBoxArrival);
        float aloneScore = ScoreOf(alone, RunKind.LateBoxArrival);

        // Now the board carries a team-mate announcing the same region and the same meeting.
        var board = new RunAdvertisementBoard();
        var matePath = mine with { Kind = RunKind.LateBoxArrival };
        board.Publish(TeamId.Home, OtherMateId,
            new RunIntent(matePath, TacticalJob.DepthRun, 200), obs.Tick - RunAdvertisementBoard.CommunicationDelayTicks);

        var withBoard = new RunCandidateGenerator(Pitch, board);
        Assert.True(withBoard.Generate(obs, 46f, obs.Tick, 4) > 0);
        float crowdedFit = FitOf(withBoard, RunKind.LateBoxArrival);
        float crowdedScore = ScoreOf(withBoard, RunKind.LateBoxArrival);

        Assert.True(crowdedFit < aloneFit,
            $"hearing a team-mate at the same post and time must lower the fit ({crowdedFit:F2} vs {aloneFit:F2})");
        Assert.True(crowdedScore < aloneScore,
            $"and the path must be worth less ({crowdedScore:F3} vs {aloneScore:F3})");
    }

    // ---- fixtures ---------------------------------------------------------------------------------

    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int OtherMateId = 10;
    private static readonly Vector2 CarrierXZ = new(0f, 10f);

    private static RunPath BoxPath(Vector2 meet, int meetTick) => new(
        RunKind.LateBoxArrival,
        StartXZ: new Vector2(meet.X - 8f, meet.Y),
        MeetXZ: meet,
        MeetRadiusMeters: 4.5f,
        StartTick: 400,
        MeetTick: meetTick,
        ExpiresTick: meetTick + 60,
        HoldUntilTick: 400,
        HoldXZ: new Vector2(meet.X - 8f, meet.Y),
        Confidence01: 0.8f);

    private static TeamBoxArrivals Arrivals(params (Vector2 Region, int MeetTick)[] arrivals)
    {
        Vector2 first = default, second = default, third = default;
        int firstTick = 0, secondTick = 0, thirdTick = 0;
        int count = Math.Min(3, arrivals.Length);

        for (int i = 0; i < count; i++)
        {
            switch (i)
            {
                case 0: first = arrivals[i].Region; firstTick = arrivals[i].MeetTick; break;
                case 1: second = arrivals[i].Region; secondTick = arrivals[i].MeetTick; break;
                default: third = arrivals[i].Region; thirdTick = arrivals[i].MeetTick; break;
            }
        }

        return new TeamBoxArrivals(first, firstTick, second, secondTick, third, thirdTick, count);
    }

    /// <summary>A striker holding shape in the final third, with a carrier behind him.</summary>
    private static FootballerObservation BoxObservation(float composure = 0.80f, float strength = 0.78f) => new()
    {
        SelfId = MateId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Striker,
        SelfPosition = new Vector3(30f, 0f, 6f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(0f, 0.11f, 10f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(30f, 0f, 6f),
        TacticalJob = TacticalJob.DepthRun,
        TacticalJobAnchor = new Vector3(30f, 0f, 6f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 300,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(0f, 0f, 10f), Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            },
            new(25, TeamId.Away, TacticalRole.CenterBack, new Vector3(44f, 0f, 4f), Vector3.Zero, 0.95f)
            {
                Capability = CapabilityEstimate.Generic
            }
        },
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes { Composure01 = composure, Strength01 = strength },
        TeamSecurity = Secure
    };

    private static RunPath PathOfKind(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.PathAt(i);
        }

        throw new Xunit.Sdk.XunitException($"the set offers no {kind} path");
    }

    private static float FitOf(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.TermsAt(i).ArrivalFit01;
        }

        throw new Xunit.Sdk.XunitException($"the set offers no {kind} path");
    }

    private static float ScoreOf(RunCandidateGenerator generator, RunKind kind)
    {
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind == kind) return generator.TermsAt(i).Score;
        }

        throw new Xunit.Sdk.XunitException($"the set offers no {kind} path");
    }
}
