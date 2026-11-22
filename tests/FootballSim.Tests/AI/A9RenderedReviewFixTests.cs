using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A9.6 — the rendered-review fixes at the decision level (owner report 2026-11-22, `ai52-rs1`).
///
/// The owner's four findings were: (1) a closing defender did not stop the carrier and an open pass was ignored;
/// (2) every attack went down the wing; (3) nobody was ever in the box to cross to; (4) shots were near-maximum
/// power and badly placed. These pin the carrier's contact risk and the shot model; the attack-variety and box
/// support levers are duty/geometry changes whose evidence is the byline scenario and the re-captured match.
/// </summary>
public sealed class A9RenderedReviewFixTests
{
    private const int CarrierId = 8;
    private const int MateId = 9;
    private const int DefenderId = 12;
    private const int KeeperId = 50;
    private static readonly PitchDimensions Pitch = new();
    private static readonly Vector3 GoalCentre = new(Pitch.HalfLength, 0f, 0f);

    /// <summary>
    /// Finding 1, the snapshot's own arithmetic: with a defender inside contact range the carry used to keep
    /// <c>P 1.00, L 0.00</c> and beat a real pass. It now pays the contact, so the open pass wins.
    /// </summary>
    [Fact]
    public void AClosingDefender_MakesCarryingLoseToAnOpenPass()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(Observation(defenderZ: -1.4f), GoalCentre, offsideLineProjX: 40f);

        float carry = UtilityOf(generator, CandidateKind.Carry);
        float pass = UtilityOf(generator, CandidateKind.Pass);

        Assert.True(pass > carry,
            $"the open pass ({pass:F2}) has to beat carrying into a defender at 1.4 m ({carry:F2})");
    }

    /// <summary>
    /// The risk is contact, not shadow: with nobody near, the carry keeps the value it always had (this is the
    /// guard against "just make carrying weak", which would collapse the reference match to one-touch passes).
    /// </summary>
    [Fact]
    public void AFreeCarrier_KeepsTheCarryAsTheBestAction()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        generator.GenerateOnBallCandidates(Observation(defenderZ: -6f), GoalCentre, offsideLineProjX: 40f);

        float carry = UtilityOf(generator, CandidateKind.Carry);
        Assert.True(carry > 0.8f, $"a free carry must keep its value, got {carry:F2}");
    }

    /// <summary>
    /// Finding 4, angle: a shot that sees a sliver of goal is worth far less than the same shot from a central
    /// position. The default (no angle supplied) keeps every older caller bit-identical.
    /// </summary>
    [Fact]
    public void AZeroAngleShot_IsWorthFarLessThanACentralOne()
    {
        float central = ActionValueScale.ShotValue(17f, 1f, ActionValueScale.ShotMouthAngleFullDegrees);
        float byline = ActionValueScale.ShotValue(17f, 1f, 9f);

        Assert.True(byline < central * 0.5f,
            $"the 9-degree shot ({byline:F2}) has to be worth far less than the central one ({central:F2})");
        Assert.Equal(ActionValueScale.ShotValue(17f, 1f), central, precision: 4);
    }

    /// <summary>
    /// Finding 4, power: execution's <c>ShotTargetHeight</c> puts power above ~0.73 over the 2.44 m bar. The
    /// whole AI band must stay under it, from the six-yard box to the edge of range.
    /// </summary>
    [Fact]
    public void AiShotPower_StaysUnderTheExecutionBar()
    {
        for (float distance = 6f; distance <= ActionValueScale.ShotRangeMeters; distance += 1.5f)
        {
            float power = ActionValueScale.ShotPowerForDistance(distance);
            Assert.InRange(power, 0.4f, 0.73f);
        }
    }

    /// <summary>
    /// Finding 4, the re-shoot: after choosing a shot the body is recovering, so the rebound of the parry (which
    /// comes straight back at the shooter) is not offered as a second shot until the timer runs out.
    /// </summary>
    [Fact]
    public void AfterChoosingAShot_NoSecondShotIsOfferedUntilTheRecoveryExpires()
    {
        var generator = new CandidateGenerator(Pitch) { TimeOnBall = 0.8f };
        FootballerObservation observation = ShootingObservation();

        generator.GenerateOnBallCandidates(observation, GoalCentre, offsideLineProjX: 40f);
        CandidateKey shotKey = Assert.Single(CollectKeys(generator, CandidateKind.Shot));

        generator.NoteDecision(observation, shotKey);
        generator.Clear();
        generator.GenerateOnBallCandidates(observation, GoalCentre, offsideLineProjX: 40f);
        Assert.Empty(CollectKeys(generator, CandidateKind.Shot));

        generator.TickTimers(0.6f);
        generator.Clear();
        generator.GenerateOnBallCandidates(observation, GoalCentre, offsideLineProjX: 40f);
        Assert.Single(CollectKeys(generator, CandidateKind.Shot));
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Carrier on the halfway line facing goal, an open mate ahead-left, one defender behind.</summary>
    private static FootballerObservation Observation(float defenderZ) => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.CentralMidfielder,
        SelfPosition = new Vector3(0f, 0f, 0f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(0.4f, 0.11f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = Vector3.Zero,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(MateId, TeamId.Home, TacticalRole.Striker,
                new Vector3(12f, 0f, 0f), Vector3.Zero, 0.95f) { Capability = CapabilityEstimate.Generic },
            new(DefenderId, TeamId.Away, TacticalRole.CenterBack,
                new Vector3(0f, 0f, defenderZ), Vector3.Zero, 0.95f) { Capability = CapabilityEstimate.Generic }
        },
        TeamProfile = new TeamTacticalProfile(),
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CentralMidfielder),
        SelfAttributes = new PlayerAttributes()
    };

    /// <summary>Carrier in shooting range with the keeper in the goal, nobody else.</summary>
    private static FootballerObservation ShootingObservation() => new()
    {
        SelfId = CarrierId,
        SelfTeam = TeamId.Home,
        SelfRole = TacticalRole.Striker,
        SelfPosition = new Vector3(40f, 0f, 0f),
        SelfVelocity = Vector3.Zero,
        SelfFacingXZ = new Vector2(1f, 0f),
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 1f,
        PerceivedBallPosition = new Vector3(40.4f, 0.11f, 0f),
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Home,
        BallLastTouchedByPlayerId = CarrierId,
        BallLastTouchedByTeam = TeamId.Home,
        BallControlState = BallControlState.Controlled,
        BallControllingPlayerId = CarrierId,
        TacticalAnchorPosition = new Vector3(40f, 0f, 0f),
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 10f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        Tick = 500,
        VisiblePlayers = new List<PerceivedEntity>
        {
            new(KeeperId, TeamId.Away, TacticalRole.Goalkeeper,
                new Vector3(50f, 0f, 0f), Vector3.Zero, 0.95f) { Capability = CapabilityEstimate.Generic }
        },
        TeamProfile = new TeamTacticalProfile(),
        SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.Striker),
        SelfAttributes = new PlayerAttributes()
    };

    private static float UtilityOf(CandidateGenerator generator, CandidateKind kind)
    {
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == kind) return generator.Candidates[i].UtilityScore;
        }

        throw new Xunit.Sdk.XunitException($"no {kind} candidate was offered");
    }

    private static List<CandidateKey> CollectKeys(CandidateGenerator generator, CandidateKind kind)
    {
        var keys = new List<CandidateKey>();
        for (int i = 0; i < generator.CandidateCount; i++)
        {
            if (generator.Candidates[i].Key.Kind == kind) keys.Add(generator.Candidates[i].Key);
        }

        return keys;
    }
}
