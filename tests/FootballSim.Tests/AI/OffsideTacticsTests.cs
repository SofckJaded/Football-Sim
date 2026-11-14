using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A5.4 — the tactical onside margin belongs before release, and the law judges the contact.
///
/// Two halves, both about the same confusion. Before the ball is played, the offside line is a
/// <b>tactical budget</b>: the law's threshold is the contact snapshot (0.15 m), while the AI's line comes
/// from its own noisy view of the last two defenders, so every point it means to use keeps
/// <see cref="OffsideTactics.MarginMeters"/> in reserve — one number, used by the pass-target gate, the run
/// region and the job target. After the ball has been played, the line is <b>history</b>: the receiver's
/// position at the contact has already been judged, so he may continue beyond it and the AI must stop
/// clamping him back. The predicate for that is a fact about the ball (a pass in flight to him), never a
/// fresh reading of the line, which is why a line that steps up after the pass cannot cancel a legal run.
/// </summary>
public sealed class OffsideTacticsTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int OtherMateId = 10;

    // ---- the release predicate ------------------------------------------------------------------

    [Fact]
    public void APlayerIsReleased_OnlyByAPassInFlightToHim()
    {
        Assert.True(OffsideTactics.IsReleased(Offside(MateId, BallControlState.InFlightPass, MateId)));
        Assert.False(OffsideTactics.IsReleased(Offside(MateId, BallControlState.InFlightPass, OtherMateId)));
        Assert.False(OffsideTactics.IsReleased(Offside(MateId, BallControlState.InFlightPass, null)));
        Assert.False(OffsideTactics.IsReleased(Offside(MateId, BallControlState.Loose, MateId)));
        Assert.False(OffsideTactics.IsReleased(Offside(MateId, BallControlState.Controlled, MateId)));
    }

    private static FootballerObservation Offside(int selfId, BallControlState state, int? intendedReceiverId) =>
        new()
        {
            SelfId = selfId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.Striker,
            SelfPosition = Vector3.Zero,
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(5f, 0.11f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Home,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Home,
            BallControlState = state,
            IntendedReceiverId = intendedReceiverId,
            TacticalAnchorPosition = Vector3.Zero,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 0f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 100,
            VisiblePlayers = Array.Empty<PerceivedEntity>(),
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
            SelfAttributes = new PlayerAttributes()
        };

    // ---- before release: one tactical margin ----------------------------------------------------

    [Fact]
    public void APassTargetInsideTheTacticalMargin_IsRefused_AndOneClearOfItIsOffered()
    {
        const float line = 30f;

        // #9 stands half a metre inside the line, #10 a metre and a half inside it. Under the old 0.35 m
        // gate both were offered: the advertisement of a "tactical" margin existed only in the run generator.
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation obs = CarrierObservation(
            Entity(MateId, new Vector2(line - 0.5f, 12f)),
            Entity(OtherMateId, new Vector2(line - 1.6f, 2f)));

        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: line);

        Assert.False(HasPassTo(generator, MateId),
            "a receiver half a metre inside the line must not be offered a pass");
        Assert.Contains(
            EnumerableRejections(generator),
            rejection => rejection.TargetPlayerId == MateId && rejection.Reason == CandidateGenerator.RejectionOffside);

        Assert.True(HasPassTo(generator, OtherMateId),
            "a receiver clear of the tactical margin must still be offered a pass");
    }

    [Fact]
    public void ThePassGateAndTheRunGenerator_KeepTheSameMargin()
    {
        Assert.Equal(OffsideTactics.MarginMeters, RunCandidateGenerator.OnsideMarginMeters);
        Assert.Equal(OffsideTactics.AdoptionToleranceMeters, RunCandidateGenerator.OnsideToleranceMeters);
        Assert.True(OffsideTactics.MarginMeters > 0.15f,
            "the tactical margin must be wider than the law's snapshot tolerance, or it is not a margin");
    }

    // ---- after release: the line stops mattering -------------------------------------------------

    [Fact]
    public void AReleasedReceiver_KeepsHisRunWhenTheLineStepsUpPastIt()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation clear = DepthObservation(tick: 100);
        RunPath adopted = AdoptInBehind(generator, clear, lineProjX: 30f);

        // The defensive line steps up past the frozen meeting region. Unreleased, this ends the run (that
        // case is pinned in RunScoringTests); released, the law has already judged him at the contact.
        FootballerObservation released = DepthObservation(
            tick: 110, BallControlState.InFlightPass, intendedReceiverId: clear.SelfId);

        generator.Generate(released, 14f, released.Tick, 4);

        RunIntent live = generator.Intent ?? throw new Xunit.Sdk.XunitException("the released run was cancelled");
        Assert.Equal(RunKind.InBehind, live.Kind);
        Assert.Equal(adopted.MeetXZ, live.Path.MeetXZ);
        Assert.Equal(clear.Tick, live.AdoptedTick);
    }

    [Fact]
    public void ABallTravellingToSomeoneElse_DoesNotReleaseThisRun()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation clear = DepthObservation(tick: 100);
        AdoptInBehind(generator, clear, lineProjX: 30f);

        FootballerObservation notForMe = DepthObservation(
            tick: 110, BallControlState.InFlightPass, intendedReceiverId: OtherMateId);

        generator.Generate(notForMe, 14f, notForMe.Tick, 4);

        Assert.True(generator.Intent is null || generator.Intent.Value.AdoptedTick != clear.Tick,
            "a pass to another player must not exempt this run from the line");
    }

    [Fact]
    public void AReleasedReceiver_IsOfferedOnlyHisFrozenRun()
    {
        var generator = new RunCandidateGenerator(Pitch);
        FootballerObservation clear = DepthObservation(tick: 100);
        AdoptInBehind(generator, clear, lineProjX: 30f);

        FootballerObservation released = DepthObservation(
            tick: 105, BallControlState.InFlightPass, intendedReceiverId: clear.SelfId);

        int count = generator.Generate(released, 30f, released.Tick, 4);

        Assert.Equal(1, count);
        Assert.Equal(RunKind.InBehind, generator.PathAt(0).Kind);
        Assert.Equal(clear.Tick, generator.Intent!.Value.AdoptedTick);
    }

    // ---- after release: movement is not clamped back ---------------------------------------------

    [Fact]
    public void AReleasedAttacker_IsNotClampedBehindTheLine()
    {
        // A striker whose shape point sits six metres beyond the line. Before release he is held a margin
        // inside it; once the ball is on its way to him the clamp would be cancelling a legal run.
        FootballerObservation clamped = ShapeObservation(released: false);
        FootballerObservation released = ShapeObservation(released: true);

        Vector2 clampedTarget = ShapeTarget(clamped, lineProjX: 20f);
        Vector2 releasedTarget = ShapeTarget(released, lineProjX: 20f);

        Assert.True(clampedTarget.X <= 20f - OffsideTactics.MarginMeters + 0.01f,
            $"an unreleased attacker must be held behind the line ({clampedTarget.X:F1})");
        Assert.True(releasedTarget.X > 20f + 1f,
            $"a released attacker must be allowed beyond the line ({releasedTarget.X:F1})");
    }

    [Fact]
    public void AReleasedJobTarget_IsNotPulledBackBehindTheLine()
    {
        FootballerObservation clamped = JobObservation(released: false);
        FootballerObservation released = JobObservation(released: true);

        Vector2 clampedTarget = JobTarget(clamped, lineProjX: 20f);
        Vector2 releasedTarget = JobTarget(released, lineProjX: 20f);

        Assert.True(clampedTarget.X <= 20f - OffsideTactics.MarginMeters + 0.01f,
            $"an unreleased job target must be held behind the line ({clampedTarget.X:F1})");
        Assert.True(releasedTarget.X > 20f + 1f,
            $"a released job target must not be pulled back ({releasedTarget.X:F1})");
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static PerceivedEntity Entity(int id, Vector2 position, Vector2 velocity = default) =>
        Entity(id, TeamId.Home, TacticalRole.CentralMidfielder, position, velocity);

    private static PerceivedEntity Entity(
        int id, TeamId team, TacticalRole role, Vector2 position, Vector2 velocity = default) =>
        new(id, team, role, new Vector3(position.X, 0f, position.Y),
            new Vector3(velocity.X, 0f, velocity.Y), 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static FootballerObservation CarrierObservation(params PerceivedEntity[] visible) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(0f, 0f, 8f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(0.4f, 0.11f, 8f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(0f, 0f, 8f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = visible,
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes()
    };

    private static List<PerceivedEntity> DepthWorld() => new()
    {
        Entity(CarrierId, TeamId.Home, TacticalRole.CentralMidfielder, new Vector2(0f, 20f)),
        Entity(25, TeamId.Away, TacticalRole.CenterBack, new Vector2(24f, 2f))
    };

    private static FootballerObservation DepthObservation(
        int tick,
        BallControlState state = BallControlState.Controlled,
        int? intendedReceiverId = null) => new()
    {
        SelfId = MateId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Striker,
        SelfPosition = new Vector3(12f, 0f, 2f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = false,
        ControlQuality01 = 0f,
        PerceivedBallPosition = new Vector3(0f, 0.11f, 20f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = state,
        BallControllingPlayerId = state == BallControlState.Controlled ? CarrierId : null,
        IntendedReceiverId = intendedReceiverId,
        TacticalAnchorPosition = new Vector3(14f, 0f, 2f),
        TacticalJob = TacticalJob.DepthRun,
        TacticalJobAnchor = new Vector3(14f, 0f, 2f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = tick,
        VisiblePlayers = DepthWorld(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
    };

    /// <summary>A striker holding shape far beyond the line, alone on the pitch.</summary>
    private static FootballerObservation ShapeObservation(bool released) => new()
    {
        SelfId = MateId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Striker,
        SelfPosition = new Vector3(18f, 0f, 4f),
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
        BallControlState = released ? BallControlState.InFlightPass : BallControlState.Controlled,
        BallControllingPlayerId = released ? null : CarrierId,
        IntendedReceiverId = released ? MateId : null,
        TacticalAnchorPosition = new Vector3(26f, 0f, 4f),
        TacticalJob = TacticalJob.Shape,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 5f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 300,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>
    /// A depth runner with nowhere to run: no space behind the line, nobody to pin, no box ahead, and level
    /// with the line. The run set is therefore empty and the duty falls back to its job anchor (A5.1), which
    /// is the non-run clamp A5.4 has to exempt after release.
    /// </summary>
    private static FootballerObservation JobObservation(bool released) => new()
    {
        SelfId = MateId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Striker,
        SelfPosition = new Vector3(18f, 0f, 10f),
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
        BallControlState = released ? BallControlState.InFlightPass : BallControlState.Controlled,
        BallControllingPlayerId = released ? null : CarrierId,
        IntendedReceiverId = released ? MateId : null,
        TacticalAnchorPosition = new Vector3(26f, 0f, 10f),
        TacticalJob = TacticalJob.DepthRun,
        TacticalJobAnchor = new Vector3(26f, 0f, 10f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 5f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 300,
        VisiblePlayers = Array.Empty<PerceivedEntity>(),
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes()
    };

    // ---- helpers --------------------------------------------------------------------------------

    private static Vector2 ShapeTarget(FootballerObservation obs, float lineProjX)
    {
        var generator = new CandidateGenerator(Pitch);
        generator.GenerateOffBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f),
            new Vector3(-Pitch.HalfLength, 0f, 0f), new Vector2(1f, 10f), isPrimaryBallResponder: false,
            offsideLineProjX: lineProjX);

        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Shape || candidate.TargetXZ is not Vector3 target) continue;
            return new Vector2(target.X, target.Z);
        }

        throw new Xunit.Sdk.XunitException("no shape candidate was offered");
    }

    private static Vector2 JobTarget(FootballerObservation obs, float lineProjX)
    {
        var generator = new CandidateGenerator(Pitch);
        generator.GenerateOffBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f),
            new Vector3(-Pitch.HalfLength, 0f, 0f), new Vector2(1f, 10f), isPrimaryBallResponder: false,
            offsideLineProjX: lineProjX);

        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind != CandidateKind.Job || candidate.Key.Adjusted) continue;
            if (candidate.TargetXZ is not Vector3 target) throw new Xunit.Sdk.XunitException("job candidate has no target");
            return new Vector2(target.X, target.Z);
        }

        throw new Xunit.Sdk.XunitException("no job candidate was offered");
    }

    private static bool HasPassTo(CandidateGenerator generator, int receiverId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == CandidateKind.Pass &&
                generator.Candidates[i].Key.TargetPlayerId == receiverId)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<CandidateRejection> EnumerableRejections(CandidateGenerator generator)
    {
        for (int i = 0; i < generator.RejectionCount; i++) yield return generator.GetRejection(i);
    }

    private static RunPath AdoptInBehind(RunCandidateGenerator generator, in FootballerObservation obs, float lineProjX)
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
}
