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
/// A7.2 — what a heard call changes.
///
/// The consumer tests build two observations that differ <b>only</b> in <see cref="FootballerObservation.Calls"/>
/// and compare the same player's candidate table. That is the milestone's "calls update belief or preference,
/// not exact hidden world state" in its most direct form: the call moves the action's preference term by the
/// documented amount and nothing else in the picture changes. The engine-level delay is
/// <see cref="TeamCallScenarioTests"/>.
/// </summary>
public sealed class TeamCallPreferenceTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int SecondMateId = 10;

    private static readonly TeamCallContext ManOnCall = new(TeamCallKind.ManOn, 7, 0, TeamCallKind.None);
    private static readonly TeamCallContext TurnCall = new(TeamCallKind.Turn, 7, 0, TeamCallKind.None);
    private static readonly TeamCallContext RunCall = new(TeamCallKind.Run, 7, 0, TeamCallKind.None);
    private static readonly TeamCallContext CoverCall = new(TeamCallKind.Cover, 103, 0, TeamCallKind.None);
    private static readonly TeamCallContext SwitchCall = new(
        TeamCallKind.Switch, SecondMateId, SecondMateId, TeamCallKind.None);

    // ---------------------------------------------------------------------------------------------
    // Man-on: shield / bounce instead of a turn into the pressure.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AManOnCall_RaisesTheShieldAndLowersTheCarry()
    {
        FootballerObservation quiet = CarrierObservation();
        FootballerObservation warned = CarrierObservation(ManOnCall);

        CandidateGenerator control = Generate(quiet);
        CandidateGenerator treated = Generate(warned);

        Assert.Equal(
            ActionValueScale.CallShieldBonus,
            UtilityOf(treated, "ShieldBall") - UtilityOf(control, "ShieldBall"),
            precision: 4);

        Assert.Equal(
            -ActionValueScale.CallCarryPenalty,
            UtilityOf(treated, "DriveWithBall") - UtilityOf(control, "DriveWithBall"),
            precision: 4);
    }

    [Fact]
    public void AManOnCall_RaisesEveryPass_AndLowersTheTakeOn()
    {
        FootballerObservation quiet = CarrierObservation();
        FootballerObservation warned = CarrierObservation(ManOnCall);

        CandidateGenerator control = Generate(quiet);
        CandidateGenerator treated = Generate(warned);

        Assert.Equal(
            ActionValueScale.CallBouncePassBonus,
            UtilityOf(treated, $"Pass({KickIntentType.GroundPass})To#{MateId}") -
            UtilityOf(control, $"Pass({KickIntentType.GroundPass})To#{MateId}"),
            precision: 4);

        Assert.Equal(
            -ActionValueScale.CallTakeOnPenalty,
            UtilityOf(treated, "TakeOnDefender") - UtilityOf(control, "TakeOnDefender"),
            precision: 4);
    }

    [Fact]
    public void ATurnCall_RaisesTheCarry()
    {
        FootballerObservation quiet = CarrierObservation();
        FootballerObservation told = CarrierObservation(TurnCall);

        CandidateGenerator control = Generate(quiet);
        CandidateGenerator treated = Generate(told);

        Assert.Equal(
            ActionValueScale.CallTurnCarryBonus,
            UtilityOf(treated, "DriveWithBall") - UtilityOf(control, "DriveWithBall"),
            precision: 4);
    }

    [Fact]
    public void AManOnCall_FlipsTheChoiceToTheShield_WhenShieldingIsTheReasonableOption()
    {
        // A carrier with an opponent on him and no clean pass: the carry is the best action until the team
        // tells him he has a man on — then the shield wins. This is "changes the relevant receiver's options"
        // at the decision level, with the delay proven by the board and engine tests.
        FootballerObservation quiet = TightCarrierObservation();
        FootballerObservation warned = TightCarrierObservation(ManOnCall);

        CandidateGenerator control = Generate(quiet);
        CandidateGenerator treated = Generate(warned);

        Assert.True(UtilityOf(control, "DriveWithBall") > UtilityOf(control, "ShieldBall"),
            "the fixture must start with the carry beating the shield");
        Assert.True(UtilityOf(treated, "ShieldBall") > UtilityOf(treated, "DriveWithBall"),
            "the man-on call must make the shield the better action");
    }

    // ---------------------------------------------------------------------------------------------
    // Switch: the pass to the team-mate who asked.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ASwitchCall_RaisesOnlyThePassToTheNamedReceiver()
    {
        FootballerObservation quiet = TwoMateObservation();
        FootballerObservation told = TwoMateObservation(SwitchCall);

        CandidateGenerator control = Generate(quiet);
        CandidateGenerator treated = Generate(told);

        Assert.Equal(
            ActionValueScale.CallSwitchPassBonus,
            PassUtility(treated, SecondMateId) - PassUtility(control, SecondMateId),
            precision: 4);
        Assert.Equal(0f, PassUtility(treated, MateId) - PassUtility(control, MateId), precision: 4);
    }

    [Fact]
    public void ASwitchCall_KeepsTheNamedReceiverInsideTheBoundedPassTargetSet()
    {
        // Two clean options; the named one is the less advanced, so with a budget of one the pre-rank
        // prunes him unless his request lifts him over the line. The pass to him must still be a real ball:
        // the budget test is about being read, not about forcing a bad pass.
        FootballerObservation quiet = SwitchBudgetObservation();
        FootballerObservation told = SwitchBudgetObservation(SwitchCall);

        var tight = new AiRuntimeConfig { MaxPassTargets = 1 };
        CandidateGenerator control = Generate(quiet, tight);
        CandidateGenerator treated = Generate(told, tight);

        Assert.False(HasPassTo(control, SecondMateId),
            "the budget of one must exclude the far-side team-mate: " + Describe(control));
        Assert.True(HasPassTo(treated, SecondMateId),
            "the switch request must rescue him from the budget: " + Describe(treated));
    }

    // ---------------------------------------------------------------------------------------------
    // Run: the runner's own run candidates.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ARunCall_RaisesTheRunCandidates()
    {
        FootballerObservation quiet = RunnerObservation();
        FootballerObservation told = RunnerObservation(RunCall);

        CandidateGenerator control = GenerateRunCandidates(quiet);
        CandidateGenerator treated = GenerateRunCandidates(told);

        Assert.Equal(control.CandidateCount, treated.CandidateCount);
        int compared = 0;
        for (int i = 0; i < control.CandidateCount; i++)
        {
            if (control.Candidates[i].Key.Kind != CandidateKind.Run) continue;
            Assert.Equal(control.Candidates[i].Key, treated.Candidates[i].Key);
            Assert.Equal(
                ActionValueScale.CallRunBonus,
                treated.Candidates[i].UtilityScore - control.Candidates[i].UtilityScore,
                precision: 4);
            compared++;
        }

        Assert.True(compared > 0, "the depth-duty fixture must offer run candidates");
    }

    // ---------------------------------------------------------------------------------------------
    // Cover: the presser's belief that somebody is behind him.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ACoverCall_ChangesThePressersBeliefAndThereforeHisStandoff()
    {
        FootballerObservation quiet = PresserObservation();
        FootballerObservation told = PresserObservation(CoverCall);

        PressSituation without = PressPolicy.BuildSituation(quiet, Pitch);
        PressSituation with = PressPolicy.BuildSituation(told, Pitch);

        Assert.False(without.HasCoverBehind, "the fixture must have nobody goal-side of the presser");
        Assert.True(with.HasCoverBehind, "the cover call is the missing fact");

        // No cover behind and a press that can be bypassed is the jockey depth; told he is covered, the
        // presser commits.
        Assert.Equal(PressPolicy.DelayStandoffMeters, PressPolicy.PressStandoffFor(without));
        Assert.Equal(PressPolicy.PressStandoffMeters, PressPolicy.PressStandoffFor(with));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A carrier with a visible opponent on him, time on the ball, and one team-mate available.</summary>
    private static FootballerObservation CarrierObservation(TeamCallContext calls = default) => Obs(
        selfXZ: new Vector2(0f, 8f),
        calls: calls,
        visible:
        [
            Opponent(201, 2.5f, 8f),
            Mate(MateId, 14f, 16f),
            Opponent(205, 26f, -14f)
        ]);

    /// <summary>The same picture, but the opponent is on top of the carrier and the passing lanes are crowded.</summary>
    private static FootballerObservation TightCarrierObservation(TeamCallContext calls = default) => Obs(
        selfXZ: new Vector2(0f, 8f),
        calls: calls,
        visible:
        [
            // A9.6a: an opponent inside ~2.2 m now carries real contact risk and the shield wins on its own, so
            // the call's flip is measured just outside that ring, where the carry is still the better action.
            Opponent(201, 2.5f, 8.2f),
            Opponent(205, 2.9f, 6.8f),
            Mate(MateId, 20f, 16f)
        ]);

    /// <summary>Two team-mates, one of them far side and open.</summary>
    private static FootballerObservation TwoMateObservation(TeamCallContext calls = default) => Obs(
        selfXZ: new Vector2(0f, 8f),
        calls: calls,
        visible:
        [
            Mate(MateId, 14f, 4f),
            Mate(SecondMateId, 10f, 24f),
            Opponent(201, 4f, 8f)
        ]);

    /// <summary>
    /// Two clean options. The advanced team-mate outranks the far-side one, so a budget of one keeps him;
    /// the far-side team-mate's request is what lifts him over that cut.
    /// </summary>
    private static FootballerObservation SwitchBudgetObservation(TeamCallContext calls = default) => Obs(
        selfXZ: new Vector2(0f, 8f),
        calls: calls,
        visible:
        [
            Mate(MateId, 16f, -4f),
            Mate(SecondMateId, 10f, 20f),
            Opponent(201, 30f, -20f)
        ]);

    /// <summary>A depth-duty runner with an announced-ish space to run into.</summary>
    private static FootballerObservation RunnerObservation(TeamCallContext calls = default) => new()
    {
        Calls = calls,
        SelfId = 9,
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
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalJob = TacticalJob.DepthRun,
        TacticalJobAnchor = new Vector3(14f, 0f, 2f),
        TacticalAnchorPosition = new Vector3(14f, 0f, 2f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 0f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        IsDesignatedBallResponder = false,
        Tick = 100,
        VisiblePlayers =
        [
            Mate(CarrierId, 0f, 20f),
            Opponent(25, 24f, 2f)
        ],
        TeamProfile = TeamTacticalProfile.PossessionControl,
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes(),
        TeamSecurity = new RestDefence(3, 1f, 0.2f, 0.2f, 0.1f, 0.7f)
    };

    /// <summary>A presser with nobody behind him, a carrier ahead and an opponent running past him.</summary>
    private static FootballerObservation PresserObservation(TeamCallContext calls = default)
    {
        var visible = new List<PerceivedEntity>
        {
            Opponent(201, 10f, 0f),
            Opponent(205, -12f, 0f),
            Mate(103, 8f, 12f)
        };

        return new FootballerObservation
        {
            Calls = calls,
            SelfId = 4,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CenterBack,
            SelfPosition = new Vector3(0f, 0f, 0f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = false,
            ControlQuality01 = 0f,
            PerceivedBallPosition = new Vector3(10f, 0.11f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Away,
            BallLastTouchedByPlayerId = 201,
            BallLastTouchedByTeam = TeamId.Away,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = 201,
            IsDesignatedBallResponder = true,
            BallResponderPlayerId = 4,
            TacticalJob = TacticalJob.Presser,
            TacticalJobAnchor = new Vector3(0f, 0f, 0f),
            TacticalAnchorPosition = new Vector3(0f, 0f, 0f),
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 10f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            IsSetPieceTaker = false,
            Tick = 200,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack),
            SelfAttributes = new PlayerAttributes()
        };
    }

    private static FootballerObservation Obs(
        Vector2 selfXZ,
        IReadOnlyList<PerceivedEntity> visible,
        TeamCallContext calls = default) => new()
    {
        Calls = calls,
        TacticalAnchorPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(selfXZ.X, 0f, selfXZ.Y),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(selfXZ.X + 0.4f, 0.11f, selfXZ.Y),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
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

    private static CandidateGenerator Generate(FootballerObservation obs, AiRuntimeConfig? ai = null)
    {
        var generator = new CandidateGenerator(Pitch, communication: null, ai) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(obs, new Vector3(Pitch.HalfLength, 0f, 0f), offsideLineProjX: 34f);
        return generator;
    }

    private static CandidateGenerator GenerateRunCandidates(FootballerObservation obs)
    {
        var generator = new CandidateGenerator(Pitch, communication: null, AiRuntimeConfig.Default);
        generator.GenerateOffBallCandidates(
            obs, new Vector3(Pitch.HalfLength, 0f, 0f), new Vector3(-Pitch.HalfLength, 0f, 0f),
            new Vector2(14f, 2f), isPrimaryBallResponder: false, offsideLineProjX: 34f);
        return generator;
    }

    private static float UtilityOf(CandidateGenerator generator, string actionName)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (string.Equals(generator.Candidates[i].ActionName, actionName, StringComparison.Ordinal))
                return generator.Candidates[i].UtilityScore;
        }

        throw new Xunit.Sdk.XunitException($"no candidate '{actionName}' was offered");
    }

    private static float PassUtility(CandidateGenerator generator, int receiverId)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            UtilityCandidate candidate = generator.Candidates[i];
            if (candidate.Key.Kind == CandidateKind.Pass && candidate.Key.TargetPlayerId == receiverId)
                return candidate.UtilityScore;
        }

        throw new Xunit.Sdk.XunitException($"no pass to #{receiverId} was offered");
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

    private static string Describe(CandidateGenerator generator)
    {
        var names = new List<string>();
        for (int i = 0; i < generator.CandidateCount; i++) names.Add(generator.Candidates[i].ActionName);
        return string.Join(" | ", names);
    }

    private static PerceivedEntity Mate(int id, float x, float z) =>
        new(id, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };

    private static PerceivedEntity Opponent(int id, float x, float z) =>
        new(id, TeamId.Away, TacticalRole.CenterBack, new Vector3(x, 0f, z), Vector3.Zero, 0.95f)
        {
            Capability = CapabilityEstimate.Generic
        };
}
