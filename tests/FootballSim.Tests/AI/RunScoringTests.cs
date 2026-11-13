using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.2 — pricing a path, and holding it.
///
/// Two halves, both required by the milestone. The first is the score: each of the seven terms is proven
/// monotone on its own, with everything else held constant, so a term cannot be inert or inverted. The
/// second is the intent lifecycle: a run that has been adopted freezes its meeting region for its window
/// (that is what stops the start/stop oscillation the exit gate names), and it ends for a stated reason —
/// expiry, another choice, a duty change, or a meeting region that is no longer legally receivable.
/// </summary>
public sealed class RunScoringTests
{
    private static readonly PitchDimensions Pitch = new();

    // ---- helpers -------------------------------------------------------------------------------

    private static PerceivedEntity Entity(
        int id,
        TeamId team,
        TacticalRole role,
        float x,
        float z,
        Vector3? velocity = null,
        CapabilityEstimate? capability = null) =>
        new(id, team, role, new Vector3(x, 0f, z), velocity ?? Vector3.Zero, 0.95f)
        {
            Capability = capability ?? CapabilityEstimate.Generic
        };

    private static PerceivedEntity Fast(int id, float x, float z) => Entity(
        id, TeamId.Away, TacticalRole.CenterBack, x, z,
        capability: new CapabilityEstimate(10.5f, 9f, 11f, 0.9f, 0.8f));

    private static PerceivedEntity Slow(int id, float x, float z) => Entity(
        id, TeamId.Away, TacticalRole.CenterBack, x, z,
        capability: new CapabilityEstimate(6.0f, 4.5f, 7f, 0.7f, 0.8f));

    private static FootballerObservation Observation(
        TacticalRole role,
        Vector2 selfXZ,
        IReadOnlyList<PerceivedEntity> visible,
        RestDefence security = default,
        Vector2? ballXZ = null,
        int tick = 100,
        TacticalJob duty = TacticalJob.DepthRun)
    {
        Vector2 ball = ballXZ ?? new Vector2(0f, 20f);
        return new FootballerObservation
        {
            SelfId = 9,
            SelfTeam = TeamId.Home,
            SelfRole = role,
            SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(ball.X, 0.11f, ball.Y),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = 8,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 8,
            TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            TacticalJob = duty,
            TacticalJobAnchor = new Vector3(selfXZ.X, 0f, selfXZ.Y),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 0f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = tick,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(role),
            SelfAttributes = new PlayerAttributes(),
            TeamSecurity = security
        };
    }

    private static RunPath Path(
        Vector2 start,
        Vector2 meet,
        RunKind kind = RunKind.InBehind,
        float radius = 5f,
        float confidence = 0.6f) =>
        new(kind, start, meet, radius,
            StartTick: 100, MeetTick: 220, ExpiresTick: 340, HoldUntilTick: 100, HoldXZ: start,
            Confidence01: confidence);

    private static readonly RestDefence Secure = new(3, 1f, 0.2f, 0.2f, 0.1f, 0.8f);
    private static readonly RestDefence Exposed = new(0, 4f, 0.8f, 0.8f, 0.8f, 0.2f);

    // ---- the seven terms --------------------------------------------------------------------------

    [Fact]
    public void AnOpenRun_BeatsACongestedOne()
    {
        Vector2 start = new(0f, 20f);
        Vector2 meet = new(20f, 20f);
        RunPath path = Path(start, meet);

        FootballerObservation open = Observation(TacticalRole.Striker, start,
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 20f, 33f) }, Secure);
        FootballerObservation crowded = Observation(TacticalRole.Striker, start,
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 21f, 21f) }, Secure);

        RunScoreTerms openTerms = RunScoring.Score(path, open, new Vector2(0f, 20f), Secure);
        RunScoreTerms crowdedTerms = RunScoring.Score(path, crowded, new Vector2(0f, 20f), Secure);

        Assert.True(openTerms.Congestion01 > crowdedTerms.Congestion01,
            $"room {openTerms.Congestion01:F2} vs {crowdedTerms.Congestion01:F2}");
        Assert.True(openTerms.Score > crowdedTerms.Score,
            $"the open run ({openTerms.Score:F2}) must beat the crowded one ({crowdedTerms.Score:F2})");
    }

    [Fact]
    public void AQuickerDefender_MakesTheArrivalRaceWorse()
    {
        Vector2 start = new(0f, 20f);
        Vector2 meet = new(20f, 20f);

        FootballerObservation slow = Observation(TacticalRole.Striker, start,
            new[] { Slow(25, 40f, 20f) }, Secure);
        FootballerObservation fast = Observation(TacticalRole.Striker, start,
            new[] { Fast(25, 40f, 20f) }, Secure);

        float slowAdvantage = RunScoring.ArrivalAdvantage(slow, meet, mySeconds: 1.6f);
        float fastAdvantage = RunScoring.ArrivalAdvantage(fast, meet, mySeconds: 1.6f);

        Assert.True(fastAdvantage < slowAdvantage,
            $"a quicker defender must reduce the arrival advantage ({fastAdvantage:F2} vs {slowAdvantage:F2})");
        Assert.InRange(fastAdvantage, 0f, 1f);
    }

    [Fact]
    public void NobodyKnownIsNotProofOfSpace()
    {
        Vector2 meet = new(20f, 20f);
        FootballerObservation empty = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            Array.Empty<PerceivedEntity>(), Secure);

        Assert.Equal(RunScoring.NeutralArrivalAdvantage, RunScoring.ArrivalAdvantage(empty, meet, 1.6f));
    }

    [Fact]
    public void AnOpponentSittingOnTheLane_LowersPassAvailability()
    {
        Vector2 carrier = new(0f, 20f);
        Vector2 meet = new(18f, 20f);

        FootballerObservation clear = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 18f, 34f) }, Secure);
        FootballerObservation blocked = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 9f, 20f) }, Secure);

        float clear01 = RunScoring.PassAvailability(clear, carrier, meet);
        float blocked01 = RunScoring.PassAvailability(blocked, carrier, meet);

        Assert.True(clear01 > blocked01, $"clear lane {clear01:F2} must beat blocked {blocked01:F2}");
        Assert.True(blocked01 < 0.5f, $"a defender standing on the lane is not a 50 % pass ({blocked01:F2})");
    }

    [Fact]
    public void ARunBeyondTheUsefulRange_IsLessAvailable()
    {
        Vector2 carrier = new(0f, 0f);
        FootballerObservation obs = Observation(TacticalRole.Striker, new Vector2(0f, 0f),
            Array.Empty<PerceivedEntity>(), Secure);

        float near = RunScoring.PassAvailability(obs, carrier, new Vector2(18f, 0f));
        float far = RunScoring.PassAvailability(obs, carrier, new Vector2(42f, 0f));

        Assert.True(near > far, $"a short run into space is more available ({near:F2}) than a 42 m one ({far:F2})");
        Assert.InRange(near, 0f, 1f);
        Assert.InRange(far, 0f, 1f);
    }

    [Fact]
    public void ARunTowardGoal_ScoresHigherThanOneAcrossIt()
    {
        Vector2 start = new(10f, 20f);
        RunPath atGoal = Path(start, new Vector2(24f, 20f));
        RunPath across = Path(start, new Vector2(10f, 34f));

        float towardGoal = RunScoring.ReceivingOrientation(atGoal, 1f);
        float sideways = RunScoring.ReceivingOrientation(across, 1f);

        Assert.True(towardGoal > sideways,
            $"arriving at the goal ({towardGoal:F2}) must beat arriving across it ({sideways:F2})");
        Assert.InRange(towardGoal, 0f, 1f);
    }

    [Fact]
    public void ARunThatBreaksALine_BeatsOneThatDoesNot()
    {
        Vector2 carrier = new(0f, 20f);
        RunPath path = Path(new Vector2(0f, 20f), new Vector2(22f, 20f));

        FootballerObservation through = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 11f, 21f) }, Secure);
        FootballerObservation around = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            new[] { Entity(25, TeamId.Away, TacticalRole.CenterBack, 11f, 34f) }, Secure);

        float broken = RunScoring.LineBreaks(through, carrier, path.MeetXZ);
        float notBroken = RunScoring.LineBreaks(around, carrier, path.MeetXZ);

        Assert.True(broken > notBroken, $"a defender on the lane is a line broken ({broken:F2} vs {notBroken:F2})");
        // One man is half of the saturation (two opponents), not a whole line on his own.
        Assert.Equal(0.5f, broken, 3);
    }

    [Fact]
    public void ARoleAppropriateRun_IsWorthMore()
    {
        float strikerRun = RunScoring.RoleUsefulness(RunKind.InBehind, TacticalRole.Striker);
        float centreBackRun = RunScoring.RoleUsefulness(RunKind.InBehind, TacticalRole.CenterBack);
        float centreBackRecycle = RunScoring.RoleUsefulness(RunKind.RecycleSupport, TacticalRole.CenterBack);

        Assert.True(strikerRun > centreBackRun + 0.5f);
        Assert.True(centreBackRecycle > centreBackRun + 0.5f);
    }

    [Fact]
    public void AnExposedSide_PaysMoreForTheSameRun()
    {
        Vector2 start = new(10f, 20f);
        RunPath path = Path(start, new Vector2(26f, 20f));

        FootballerObservation secure = Observation(TacticalRole.Striker, start,
            Array.Empty<PerceivedEntity>(), Secure);
        FootballerObservation exposed = Observation(TacticalRole.Striker, start,
            Array.Empty<PerceivedEntity>(), Exposed);

        RunScoreTerms secureTerms = RunScoring.Score(path, secure, new Vector2(0f, 20f), Secure);
        RunScoreTerms exposedTerms = RunScoring.Score(path, exposed, new Vector2(0f, 20f), Exposed);

        Assert.True(exposedTerms.TransitionExposure01 > secureTerms.TransitionExposure01);
        Assert.True(secureTerms.Score > exposedTerms.Score,
            $"the exposed side must price the same run lower ({exposedTerms.Score:F2} vs {secureTerms.Score:F2})");
    }

    [Fact]
    public void NoRestDefencePicture_IsPricedNeutral_NotFullyExposed()
    {
        // Unknown is its own value: not "fully secure" (0) and not "fully exposed" (1).
        Assert.Equal(RunScoring.NeutralExposure, RunScoring.TransitionExposure(default));
        Assert.NotEqual(1f, RunScoring.TransitionExposure(default));
        Assert.Equal(1f - Secure.Security01, RunScoring.TransitionExposure(Secure), 3);
    }

    [Fact]
    public void DegenerateInputs_ProduceFiniteTermsRatherThanNaN()
    {
        RunPath broken = Path(new Vector2(float.NaN, 0f), new Vector2(float.NaN, 0f));
        FootballerObservation obs = Observation(TacticalRole.Striker, new Vector2(0f, 20f),
            Array.Empty<PerceivedEntity>(), Secure);

        Assert.Equal(RunScoreTerms.None, RunScoring.Score(broken, obs, new Vector2(0f, 20f), Secure));

        RunPath valid = Path(new Vector2(0f, 20f), new Vector2(22f, 20f));
        RunScoreTerms terms = RunScoring.Score(valid, obs, new Vector2(0f, 20f), Secure);
        Assert.True(float.IsFinite(terms.Score));
        Assert.InRange(terms.ArrivalAdvantage01, 0f, 1f);
        Assert.InRange(terms.PassAvailability01, 0f, 1f);
        Assert.InRange(terms.ReceivingOrientation01, 0f, 1f);
        Assert.InRange(terms.LineBreaks01, 0f, 1f);
        Assert.InRange(terms.Congestion01, 0f, 1f);
        Assert.InRange(terms.RoleUsefulness01, 0f, 1f);
        Assert.InRange(terms.TransitionExposure01, 0f, 1f);
    }

    // ---- the intent lifecycle ---------------------------------------------------------------------

    private static List<PerceivedEntity> DepthWorld() => new()
    {
        Entity(8, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 20f),
        Entity(25, TeamId.Away, TacticalRole.CenterBack, 24f, 2f)
    };

    private static FootballerObservation DepthObservation(
        Vector2? ballXZ = null,
        int tick = 100,
        TacticalJob duty = TacticalJob.DepthRun) =>
        Observation(TacticalRole.Striker, new Vector2(12f, 2f), DepthWorld(), Secure, ballXZ, tick, duty);

    private static RunPath AdoptInBehind(RunCandidateGenerator generator, in FootballerObservation obs,
        float lineProjX = 30f)
    {
        Assert.True(generator.Generate(obs, lineProjX, obs.Tick, 4) > 0);
        RunPath chosen = default;
        bool found = false;
        for (int i = 0; i < generator.Count; i++)
        {
            if (generator.PathAt(i).Kind != RunKind.InBehind) continue;
            chosen = generator.PathAt(i);
            found = true;
            break;
        }

        Assert.True(found, "the depth world must offer an in-behind path");
        generator.NoteSelected(obs, RunTags.KeyOf(RunKind.InBehind));
        return chosen;
    }

    [Fact]
    public void TheAdoptedRun_FreezesItsMeetingRegion_WhileTheBallMoves()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation first = DepthObservation();
        RunPath adopted = AdoptInBehind(generator, first);

        Assert.NotNull(generator.Intent);
        Assert.Equal(RunKind.InBehind, generator.Intent!.Value.Kind);
        Assert.Equal(adopted.MeetXZ, generator.Intent.Value.Path.MeetXZ);

        // The ball moves, the picture changes, the player re-decides ten ticks later: the meeting region
        // must be exactly the one he adopted, or the run never arrives anywhere.
        for (int step = 1; step <= 10; step++)
        {
            FootballerObservation later = DepthObservation(
                new Vector2(step * 0.6f, 20f - step * 0.4f), first.Tick + step);
            generator.Generate(later, 30f, later.Tick, 4);

            RunPath frozen = default;
            bool found = false;
            for (int i = 0; i < generator.Count; i++)
            {
                if (generator.PathAt(i).Kind != RunKind.InBehind) continue;
                frozen = generator.PathAt(i);
                found = true;
            }

            Assert.True(found, $"the adopted run disappeared at step {step}");
            Assert.Equal(adopted.MeetXZ, frozen.MeetXZ);
            Assert.Equal(adopted.ExpiresTick, frozen.ExpiresTick);
        }
    }

    [Fact]
    public void TheAdoptedRun_EndsWhenItsWindowCloses()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation first = DepthObservation();
        RunPath adopted = AdoptInBehind(generator, first);

        FootballerObservation expired = DepthObservation(tick: adopted.ExpiresTick + 1);
        generator.Generate(expired, 30f, expired.Tick, 4);

        // The intent is gone (a fresh path may well be offered for the new window, but it is not the old
        // one) and its adoption is not silently re-timed.
        Assert.True(generator.Intent is null || generator.Intent.Value.AdoptedTick > first.Tick);
        Assert.NotEqual(first.Tick, generator.Intent?.AdoptedTick ?? -1);
    }

    [Fact]
    public void ChoosingSomethingElse_EndsTheRun()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = DepthObservation();
        AdoptInBehind(generator, obs);
        Assert.NotNull(generator.Intent);

        generator.NoteSelected(obs, new CandidateKey(CandidateKind.Shape, TargetTag: "support"));

        Assert.Null(generator.Intent);
    }

    [Fact]
    public void ADutyChange_EndsTheRun()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = DepthObservation();
        AdoptInBehind(generator, obs);
        Assert.NotNull(generator.Intent);

        FootballerObservation otherDuty = DepthObservation(duty: TacticalJob.NearOutlet);
        generator.Generate(otherDuty, 30f, otherDuty.Tick, 4);

        Assert.Null(generator.Intent);
    }

    [Fact]
    public void ARunThatIsNoLongerLegallyReceivable_Ends()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = DepthObservation();
        AdoptInBehind(generator, obs);
        Assert.NotNull(generator.Intent);

        // The defensive line steps up past the meeting region: before the ball is released that run is
        // offside, so it cannot stay adopted (A5.4 puts the margin here, the contact snapshot later).
        generator.Generate(obs, 14f, obs.Tick, 4);

        Assert.Null(generator.Intent);
    }

    [Fact]
    public void ARunChosenTwice_KeepsItsAdoptionTick()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation obs = DepthObservation();
        AdoptInBehind(generator, obs);
        int adoptedAt = generator.Intent!.Value.AdoptedTick;

        generator.NoteSelected(DepthObservation(tick: obs.Tick + 5), RunTags.KeyOf(RunKind.InBehind));

        Assert.Equal(adoptedAt, generator.Intent!.Value.AdoptedTick);
    }

    // ---- the controller integration ---------------------------------------------------------------

    [Fact]
    public void TheController_AdoptsTheRunItChose_AndHoldsItLikeAJob()
    {
        var controller = new TacticalUtilityAiController(Pitch);

        // The first decisions build up the commitment; a run path must be what the player ends up doing.
        UtilityCandidate last = default;
        float windowAtSelection = -1f;
        for (int i = 0; i < 12; i++)
        {
            controller.Decide(DepthObservation(tick: 100 + i), 1f / 60f);
            last = controller.LastSelected;
            if (windowAtSelection < 0f && controller.CommittedKey.Kind == CandidateKind.Run)
                windowAtSelection = controller.CommitmentRemainingSeconds;
        }

        Assert.Equal(CandidateKind.Run, last.Key.Kind);
        Assert.NotNull(controller.Runs.Intent);
        Assert.Equal(last.Key, controller.CommittedKey);

        // A run is a promise, not a reflex: the tick it was adopted it gets the job commitment window.
        Assert.Equal(controller.RuntimeConfig.JobCommitmentSeconds, windowAtSelection, 3);
    }
}
