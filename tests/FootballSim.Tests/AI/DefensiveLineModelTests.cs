using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.3 — the defensive line's shared depth band, at the model level.
///
/// Every fixture is a team of outfield players whose formation anchors are known, an opponent picture the
/// team is allowed to believe, and the ball. Each test moves exactly one of the four inputs the plan names
/// (field position, passer pressure, a credible run, keeper sweep coverage) and asserts the band's response;
/// the last group pins the local exceptions and the zone/man style mix.
/// </summary>
public sealed class DefensiveLineModelTests
{
    private const int HomeKeeperId = 101;
    private const int HomeCbA = 102;   // holds the negative-Z slot
    private const int HomeCbB = 103;   // holds the positive-Z slot
    private const int HomeFbA = 104;
    private const int AwayCarrierId = 201;
    private const int AwayRunnerId = 202;

    private static readonly PitchDimensions Pitch = new();

    // ---------------------------------------------------------------------------------------------
    // Field position.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The first input: the ball's own depth. With the formation anchors held fixed — the only difference
    /// is where the ball is — the band sits higher when the ball is in the opponent's half and deeper when
    /// it is in this team's own third. (The clamp at the deep end is the ball cap, which is why this asserts
    /// a direction and a size, not an exact number.)
    /// </summary>
    [Fact]
    public void TheBandsDepth_FollowsTheBall()
    {
        List<FootballerState> players = LineAt(-20f);

        DefensiveLineBand high = Evaluate(players, ballX: 30f, Belief(), BallPossessionSnapshot.Loose);
        DefensiveLineBand deep = Evaluate(players, ballX: -30f, Belief(), BallPossessionSnapshot.Loose);

        Assert.True(high.IsValid && deep.IsValid);
        Assert.True(high.FieldShiftMeters > 4f, $"the field term was only {high.FieldShiftMeters:F1} m with the ball at +30");
        Assert.True(deep.FieldShiftMeters < -4f, $"the field term was only {deep.FieldShiftMeters:F1} m with the ball at -30");
        Assert.True(high.ProjX > deep.ProjX + 8f,
            $"the band moved only {high.ProjX - deep.ProjX:F1} m between the two halves of the pitch");
        Assert.True(high.ProjX > high.AnchorMeanProjX, "the ball in the opponent's half must squeeze the line up");
        Assert.True(deep.ProjX < deep.AnchorMeanProjX, "the ball in the own third must drop the line");
    }

    /// <summary>
    /// The ball cap: the line is never ahead of the ball by more than the documented tolerance. A ball on
    /// this team's own byline must not leave a defensive line standing upfield of it.
    /// </summary>
    [Fact]
    public void TheLine_IsNeverWellAheadOfTheBall()
    {
        List<FootballerState> players = LineAt(-12f);
        DefensiveLineBand band = Evaluate(players, ballX: -44f, Belief(), BallPossessionSnapshot.Loose);

        Assert.True(band.IsValid);
        Assert.True(band.ProjX <= -44f + DefensiveLineModel.AheadOfBallToleranceMeters + 0.01f,
            $"the line stood at {band.ProjX:F1} m with the ball at -44 m");
        Assert.True(band.BallCapShiftMeters < 0f, "the ball cap should have moved the band");
    }

    // ---------------------------------------------------------------------------------------------
    // Passer pressure.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The second input: passer pressure, read through the one arrival model. The same line, the same ball
    /// position, the same opponents — only the carrier's distance from the nearest defender changes, so
    /// only the pressure term can differ, and the band must step up when he is pressed.
    /// </summary>
    [Fact]
    public void APressuredPasser_PushesTheLineUp()
    {
        List<FootballerState> players = LineAt(-25f);
        var carrier = Player(AwayCarrierId, TeamId.Away, TacticalRole.CentralMidfielder, -10f, 0f);

        var possession = new BallPossessionSnapshot(
            BallControlState.Controlled, TeamId.Away, AwayCarrierId, null);

        TeamBelief free = Belief(carrier);
        DefensiveLineBand freeBand = Evaluate(players, ballX: -10f, free, possession);

        // The carrier now stands on the line's toes: the best-placed defender arrives in half a second.
        var pressedCarrier = Player(AwayCarrierId, TeamId.Away, TacticalRole.CentralMidfielder, -24f, 6f);
        TeamBelief pressed = Belief(pressedCarrier);
        DefensiveLineBand pressedBand = Evaluate(players, ballX: -24f + 0.4f, pressed, possession);

        Assert.True(freeBand.PasserPressure01 < 0.1f, $"a carrier 15 m away is not pressed ({freeBand.PasserPressure01:F2})");
        Assert.True(pressedBand.PasserPressure01 > 0.7f, $"a carrier 3 m away must be pressed ({pressedBand.PasserPressure01:F2})");
        Assert.True(pressedBand.PressureShiftMeters > 3f);
        Assert.True(freeBand.PressureShiftMeters < -3f);
        Assert.True(pressedBand.ProjX > freeBand.ProjX + 4f,
            $"pressure moved the band only {pressedBand.ProjX - freeBand.ProjX:F1} m");
    }

    /// <summary>
    /// An opponent nobody on this team has seen gives no pressure estimate at all: the band stays where the
    /// anchors put it rather than pretending the man is being closed down (the control state is a ball fact
    /// every player knows; the carrier's position is not).
    /// </summary>
    [Fact]
    public void AnUnbelievedCarrier_LeavesTheBandUnpushed()
    {
        List<FootballerState> players = LineAt(-25f);
        var possession = new BallPossessionSnapshot(
            BallControlState.Controlled, TeamId.Away, AwayCarrierId, null);

        DefensiveLineBand band = Evaluate(players, ballX: -10f, Belief(), possession);

        Assert.True(band.IsValid);
        Assert.InRange(band.PasserPressure01, 0f, 0.36f);
        Assert.True(MathF.Abs(band.PressureShiftMeters) < 3f);
    }

    // ---------------------------------------------------------------------------------------------
    // Credible runs.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The third input, and the one that is a <b>cap</b> rather than a shift: a high line is refused when a
    /// believed opponent is running at this goal, and the same man standing still or running away does not
    /// cap anything. Only the velocity differs between the three worlds.
    /// </summary>
    [Fact]
    public void ACredibleRunInBehind_CapsTheLine()
    {
        List<FootballerState> players = HighLine();
        BallPossessionSnapshot possession = BallPossessionSnapshot.Loose;

        DefensiveLineBand still = Evaluate(players, ballX: 20f,
            Belief(Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, 8f, 0f, velocity: Vector2.Zero)), possession);
        DefensiveLineBand away = Evaluate(players, ballX: 20f,
            Belief(Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, 8f, 0f, velocity: new Vector2(6f, 0f))), possession);
        DefensiveLineBand atUs = Evaluate(players, ballX: 20f,
            Belief(Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, 8f, 0f, velocity: new Vector2(-6f, 0f))), possession);

        Assert.False(still.HasCredibleRunner);
        Assert.False(away.HasCredibleRunner, "a man running away from this goal is not a run in behind");
        Assert.True(atUs.HasCredibleRunner);
        Assert.Equal(AwayRunnerId, atUs.DeepestRunnerId);
        Assert.True(atUs.ProjX <= 8f - DefensiveLineModel.LineToRunnerMeters + 0.01f,
            $"the line stood at {atUs.ProjX:F1} m with a runner at 8 m: level with him, not goal-side");
        Assert.True(atUs.ProjX < still.ProjX - 4f,
            $"the run moved the band only {still.ProjX - atUs.ProjX:F1} m");
        Assert.True(atUs.RunShiftMeters < -4f);
        Assert.Equal(still.ProjX, away.ProjX, precision: 3);
    }

    /// <summary>
    /// Two runners: the <b>deepest</b> credible one sets the ceiling, because he is the one a ball played
    /// behind the line reaches first.
    /// </summary>
    [Fact]
    public void TheDeepestCredibleRunner_SetsTheCap()
    {
        List<FootballerState> players = HighLine();
        var deep = Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, 6f, 0f, velocity: new Vector2(-5f, 0f));
        var shallow = Player(203, TeamId.Away, TacticalRole.Winger, 16f, -12f, velocity: new Vector2(-7f, 0f));

        DefensiveLineBand band = Evaluate(players, ballX: 24f,
            Belief(deep, shallow), BallPossessionSnapshot.Loose);

        Assert.True(band.HasCredibleRunner);
        Assert.Equal(AwayRunnerId, band.DeepestRunnerId);
        Assert.Equal(6f, band.DeepestRunnerProjX, precision: 2);
        Assert.True(band.ProjX <= 6f - DefensiveLineModel.LineToRunnerMeters + 0.01f);
    }

    /// <summary>
    /// A run that is only a memory is weaker, and one from long enough ago is not credible at all: the age
    /// and the confidence of the sighting both enter the threat, so a stale sighting cannot pin a line back
    /// for ever.
    /// </summary>
    [Fact]
    public void AStaleSighting_IsNotACredibleRun()
    {
        List<FootballerState> players = HighLine();

        DefensiveLineBand fresh = Evaluate(players, ballX: 20f,
            BeliefWithAge(secondsAgo: 0, confidence: 1f), BallPossessionSnapshot.Loose);
        DefensiveLineBand remembered = Evaluate(players, ballX: 20f,
            BeliefWithAge(secondsAgo: 0.5f, confidence: 0.8f), BallPossessionSnapshot.Loose);
        DefensiveLineBand ancient = Evaluate(players, ballX: 20f,
            BeliefWithAge(secondsAgo: 3f, confidence: 0.8f), BallPossessionSnapshot.Loose);

        Assert.True(fresh.HasCredibleRunner);
        Assert.True(remembered.HasCredibleRunner, "a sighting from half a second ago is still a run");
        Assert.True(remembered.RunThreat01 < fresh.RunThreat01,
            $"an older sighting must weigh less ({remembered.RunThreat01:F2} vs {fresh.RunThreat01:F2})");
        Assert.False(ancient.HasCredibleRunner, "a three-second-old sighting cannot cap the line");
        Assert.True(ancient.ProjX > fresh.ProjX);
    }

    // ---------------------------------------------------------------------------------------------
    // Keeper sweep coverage.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The fourth input: a high line is only lawful if the space behind it can be covered, so the keeper's
    /// own reach — through the one arrival model, over the sweep budget — sets a ceiling. A slow keeper
    /// refuses the same line a quick one allows, and the band reports both the reach and the pull-back.
    /// </summary>
    [Fact]
    public void AKeeperWhoCannotSweep_RefusesTheHighLine()
    {
        List<FootballerState> players = HighLine();
        players.Add(Keeper(HomeKeeperId, maxSprintSpeed: 9.5f, acceleration: 8.0f));
        DefensiveLineBand fast = Evaluate(players, ballX: 20f, Belief(), BallPossessionSnapshot.Loose);

        players[^1] = Keeper(HomeKeeperId, maxSprintSpeed: 4.0f, acceleration: 2.4f);
        DefensiveLineBand slow = Evaluate(players, ballX: 20f, Belief(), BallPossessionSnapshot.Loose);

        Assert.True(fast.KeeperReachMeters > slow.KeeperReachMeters + 3f,
            $"the reach estimate did not respond to the keeper ({fast.KeeperReachMeters:F1} m vs {slow.KeeperReachMeters:F1} m)");
        Assert.True(fast.KeeperCoverage01 > slow.KeeperCoverage01);
        Assert.True(slow.ProjX < fast.ProjX - 3f,
            $"the slow keeper allowed a line at {slow.ProjX:F1} m against the quick one's {fast.ProjX:F1} m");
        Assert.True(slow.KeeperShiftMeters < -3f, "the keeper cap should show up as a pull-back");
    }

    /// <summary>
    /// A team with no keeper in the fixture is not a team whose keeper cannot sweep: the cap does not apply
    /// at all. Put a keeper on his own goal line 58 m behind the same line and it does — which is the whole
    /// point of the input, and why the no-keeper band is the higher of the two.
    /// </summary>
    [Fact]
    public void NoKeeperInTheFixture_DoesNotApplyTheKeeperCap()
    {
        List<FootballerState> players = HighLine();
        DefensiveLineBand noKeeper = Evaluate(players, ballX: 20f, Belief(), BallPossessionSnapshot.Loose);

        players.Add(Keeper(HomeKeeperId, maxSprintSpeed: 8.4f, acceleration: 6.8f));
        DefensiveLineBand withKeeper = Evaluate(players, ballX: 20f, Belief(), BallPossessionSnapshot.Loose);

        Assert.Equal(0f, noKeeper.KeeperShiftMeters);
        Assert.Equal(0, noKeeper.KeeperId);
        Assert.True(noKeeper.ProjX > withKeeper.ProjX,
            $"a keeper 58 m behind the line must cap it ({withKeeper.ProjX:F1} m vs {noKeeper.ProjX:F1} m uncapped)");
        Assert.True(withKeeper.KeeperShiftMeters < 0f);
        Assert.True(withKeeper.KeeperCoverage01 > 0f);
    }

    // ---------------------------------------------------------------------------------------------
    // Local exceptions, bounds and the style mix.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The band is shared, the exceptions are local and bounded: the ball-side defender may step up to
    /// <see cref="DefensiveLineModel.BallSideAdvanceMeters"/>, the far-side one tucks in, and neither may
    /// leave the band's own range — so an exception cannot defeat the cap that set the ceiling.
    /// </summary>
    [Fact]
    public void LocalExceptions_AreBoundedByTheBandsRange()
    {
        DefensiveLineBand band = Band(-20f, min: -47f, max: -5f);

        Assert.Equal(-17f, DefensiveLineModel.ProjFor(band, playerZ: 0f, ballZ: 0f), precision: 3);
        Assert.Equal(-20f, DefensiveLineModel.ProjFor(band, playerZ: 18f, ballZ: 0f), precision: 3);
        Assert.Equal(-22f, DefensiveLineModel.ProjFor(band, playerZ: 30f, ballZ: 0f), precision: 3);

        // A band whose own ceiling was set by a runner: the ball-side step may not go through it.
        DefensiveLineBand capped = band with { MaxProjX = -20.5f };
        Assert.Equal(-20.5f, DefensiveLineModel.ProjFor(capped, playerZ: 0f, ballZ: 0f), precision: 3);

        // And the far-side tuck may not go below the floor.
        DefensiveLineBand floored = band with { MinProjX = -21f };
        Assert.Equal(-21f, DefensiveLineModel.ProjFor(floored, playerZ: 30f, ballZ: 0f), precision: 3);
    }

    /// <summary>
    /// A line player's lane is his own slot, not wherever his body has drifted to — so a defender a few
    /// metres out of position does not flip his local exception — unless he is genuinely playing elsewhere,
    /// in which case his body is the honest answer.
    /// </summary>
    [Fact]
    public void TheLane_IsTheSlot_UntilThePlayerIsGenuinelyElsewhere()
    {
        DefensiveLineBand band = Band(-20f, min: -47f, max: -5f);
        FootballerState player = Player(HomeCbA, TeamId.Home, TacticalRole.CenterBack, -25f, 6f);

        // Body 4 m from its slot: the slot still decides, and the slot is on the ball's side.
        Assert.Equal(-17f, DefensiveLineModel.ProjForPlayer(band, player, new Vector2(0f, 0f)), precision: 3);

        // Body 20 m from its slot: he is playing somewhere else, so his body decides.
        player.Position = new Vector3(-25f, 0f, 30f);
        Assert.Equal(-22f, DefensiveLineModel.ProjForPlayer(band, player, new Vector2(0f, 0f)), precision: 3);
    }

    /// <summary>
    /// The bounds hold on degenerate input: no line, no belief, a ball on the byline, a keeper on his own
    /// goal line. The band is always finite, inside the pitch, and min ≤ target ≤ max.
    /// </summary>
    [Fact]
    public void TheBand_IsFiniteAndInsideItsOwnBounds()
    {
        Assert.False(DefensiveLineModel.Evaluate(
            TeamId.Home, new[] { Player(HomeCbA, TeamId.Home, TacticalRole.Striker, -10f, 0f) },
            new BallState { Position = Vector3.Zero }, BallPossessionSnapshot.Loose,
            Belief(), new TeamTacticalProfile(), 1f, Pitch).IsValid);

        foreach (float ballX in new[] { -Pitch.HalfLength, -30f, 0f, 30f, Pitch.HalfLength })
        {
            List<FootballerState> players = LineAt(-30f);
            players.Add(Keeper(HomeKeeperId, maxSprintSpeed: 8.4f, acceleration: 6.8f));

            DefensiveLineBand band = Evaluate(players, ballX, Belief(), BallPossessionSnapshot.Loose);
            Assert.True(band.IsValid);
            Assert.True(float.IsFinite(band.ProjX) && float.IsFinite(band.MinProjX) && float.IsFinite(band.MaxProjX));
            Assert.True(band.MinProjX <= band.ProjX + 1e-3f && band.ProjX <= band.MaxProjX + 1e-3f);
            Assert.InRange(band.ProjX, -Pitch.HalfLength, Pitch.HalfLength);
            Assert.InRange(band.MaxProjX, -Pitch.HalfLength, Pitch.HalfLength);
            Assert.True(float.IsFinite(band.RunThreat01) && float.IsFinite(band.KeeperCoverage01));
            Assert.InRange(band.PasserPressure01, 0f, 1f);
        }
    }

    /// <summary>
    /// The band is a frame-relative fact: the same world mirrored through the halfway line, with the attack
    /// direction reversed, yields the same projection. Nothing in it is accidentally absolute in X.
    /// </summary>
    [Fact]
    public void TheBand_IsFrameRelative()
    {
        List<FootballerState> forward = LineAt(-22f);
        forward.Add(Player(HomeKeeperId, TeamId.Home, TacticalRole.Goalkeeper, -46f, 0f));
        var runner = Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, 4f, 6f, velocity: new Vector2(-5f, 0f));

        DefensiveLineBand left = Evaluate(forward, ballX: -14f, Belief(runner), BallPossessionSnapshot.Loose, attackDirX: 1f);

        var mirrored = new List<FootballerState>();
        foreach (FootballerState source in forward)
        {
            FootballerState copy = Player(source.Id, source.Team, source.Role, -source.Position.X, source.Position.Z);
            copy.TacticalAnchor = new Vector3(-source.TacticalAnchor.X, 0f, source.TacticalAnchor.Z);
            mirrored.Add(copy);
        }

        var mirroredRunner = Player(AwayRunnerId, TeamId.Away, TacticalRole.Striker, -4f, 6f, velocity: new Vector2(5f, 0f));
        DefensiveLineBand right = Evaluate(mirrored, ballX: 14f, Belief(mirroredRunner), BallPossessionSnapshot.Loose, attackDirX: -1f);

        Assert.True(left.IsValid && right.IsValid);
        Assert.Equal(left.ProjX, right.ProjX, precision: 2);
        Assert.Equal(left.AnchorMeanProjX, right.AnchorMeanProjX, precision: 2);
        Assert.Equal(left.HasCredibleRunner, right.HasCredibleRunner);
    }

    /// <summary>
    /// A6.3's zone/man mix: how many opponents a side marks is a property of its style, and the shipped
    /// presets land on distinct numbers — a direct counter-attacking side marks one, a possession side two,
    /// a gegenpressing side three — with the share monotone in the pressing intensity that drives it.
    /// </summary>
    [Fact]
    public void TheStyle_SetsHowManyOpponentsAreMarkedManForMan()
    {
        var direct = TeamTacticalProfile.DirectCounterattack;
        var possession = TeamTacticalProfile.PossessionControl;
        var gegenpress = TeamTacticalProfile.HighPressGegenpress;

        Assert.Equal(1, DefensiveLineModel.MarkerCount(direct));
        Assert.Equal(2, DefensiveLineModel.MarkerCount(possession));
        Assert.Equal(3, DefensiveLineModel.MarkerCount(gegenpress));

        Assert.True(DefensiveLineModel.ManShare01(direct) < DefensiveLineModel.ManShare01(possession));
        Assert.True(DefensiveLineModel.ManShare01(possession) < DefensiveLineModel.ManShare01(gegenpress));

        // Monotone in the one input that is supposed to drive it, holding compactness fixed.
        float previous = -1f;
        for (int step = 0; step <= 10; step++)
        {
            var profile = new TeamTacticalProfile
            {
                PressingIntensity01 = step / 10f,
                Compactness01 = 0.6f
            };

            float share = DefensiveLineModel.ManShare01(profile);
            Assert.InRange(share, 0f, 1f);
            Assert.True(share > previous);
            previous = share;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>A back four standing 20 m in front of its own goal, no keeper, no opponents.</summary>
    private static List<FootballerState> LineAt(float x) => new()
    {
        Player(HomeCbA, TeamId.Home, TacticalRole.CenterBack, x, -8f),
        Player(HomeCbB, TeamId.Home, TacticalRole.CenterBack, x, 8f),
        Player(HomeFbA, TeamId.Home, TacticalRole.FullBack, x + 2f, -22f)
    };

    /// <summary>A deliberately high line (in the opponent's half) to test the ceilings against.</summary>
    private static List<FootballerState> HighLine() => new()
    {
        Player(HomeCbA, TeamId.Home, TacticalRole.CenterBack, 8f, -8f),
        Player(HomeCbB, TeamId.Home, TacticalRole.CenterBack, 8f, 8f),
        Player(HomeFbA, TeamId.Home, TacticalRole.FullBack, 6f, -22f)
    };

    private static FootballerState Player(
        int id, TeamId team, TacticalRole role, float x, float z, Vector2 velocity = default, int shirt = 0)
    {
        var player = new FootballerState
        {
            Id = id,
            Team = team,
            ShirtNumber = shirt == 0 ? id : shirt,
            Role = role,
            Attributes = new PlayerAttributes(),
            Tendencies = PlayerTendencyProfile.ForRole(role),
            Position = new Vector3(x, 0f, z),
            Velocity = new Vector3(velocity.X, 0f, velocity.Y),
            TacticalAnchor = new Vector3(x, 0f, z),
            FacingXZ = new Vector2(team == TeamId.Home ? 1f : -1f, 0f)
        };
        return player;
    }

    /// <summary>A keeper on his line with an explicit body, because the sweep cap is about his reach.</summary>
    private static FootballerState Keeper(int id, float maxSprintSpeed, float acceleration) => new()
    {
        Id = id,
        Team = TeamId.Home,
        ShirtNumber = 1,
        Role = TacticalRole.Goalkeeper,
        Attributes = new PlayerAttributes { MaxSprintSpeed = maxSprintSpeed, Acceleration = acceleration },
        Tendencies = PlayerTendencyProfile.ForRole(TacticalRole.Goalkeeper),
        Position = new Vector3(-45f, 0f, 0f),
        TacticalAnchor = new Vector3(-45f, 0f, 0f),
        FacingXZ = new Vector2(1f, 0f)
    };

    private static TeamBelief Belief(params FootballerState[] opponents) =>
        TeamBelief.FromTruth(TeamId.Home, opponents);

    /// <summary>
    /// A belief whose one opponent is a run seen <paramref name="secondsAgo"/> ago at
    /// <paramref name="confidence"/>, built through the real observation path so the age is the one the
    /// perception system would have produced.
    /// </summary>
    private static TeamBelief BeliefWithAge(float secondsAgo, float confidence)
    {
        int ageTicks = (int)MathF.Round(secondsAgo / SimConstants.FixedDeltaTime);
        FootballerState observer = Player(HomeCbA, TeamId.Home, TacticalRole.CenterBack, -25f, -8f);

        var runner = new PerceivedEntity(
            AwayRunnerId, TeamId.Away, TacticalRole.Striker,
            new Vector3(8f, 0f, 0f), new Vector3(-6f, 0f, 0f), confidence)
        {
            ObservedAgeTicks = ageTicks,
            PositionUncertaintyMeters = ageTicks * 0.05f
        };

        FootballerObservation observation = Observation(observer, new[] { runner });

        var belief = new TeamBelief(TeamId.Home);
        belief.RebuildFromObservations(
            new[] { observer },
            new Dictionary<int, FootballerObservation> { [observer.Id] = observation });
        return belief;
    }

    private static FootballerObservation Observation(FootballerState self, IReadOnlyList<PerceivedEntity> visible) => new()
    {
        SelfId = self.Id,
        SelfTeam = self.Team,
        SelfRole = self.Role,
        SelfPosition = self.Position,
        SelfVelocity = self.Velocity,
        SelfFacingXZ = self.FacingXZ,
        Stamina01 = 1f,
        AvailableToKick = true,
        ControlQuality01 = 0f,
        PerceivedBallPosition = self.Position,
        PerceivedBallVelocity = Vector3.Zero,
        PossessingTeam = TeamId.Away,
        BallLastTouchedByPlayerId = AwayRunnerId,
        BallLastTouchedByTeam = TeamId.Away,
        TacticalAnchorPosition = self.TacticalAnchor,
        AttackingDirectionX = 1f,
        MatchTimeSeconds = 20f,
        CurrentMatchPhase = MatchPhase.FirstHalf,
        IsSetPieceTaker = false,
        VisiblePlayers = visible,
        TeamProfile = new TeamTacticalProfile(),
        SelfTendencies = self.Tendencies,
        SelfAttributes = self.Attributes
    };

    private static DefensiveLineBand Band(float proj, float min, float max) => new(
        IsValid: true,
        ProjX: proj,
        MinProjX: min,
        MaxProjX: max,
        AnchorMeanProjX: proj,
        FieldPosition01: 0.5f,
        PasserPressure01: 0.5f,
        RunThreat01: 0f,
        KeeperCoverage01: 0.5f,
        FieldShiftMeters: 0f,
        PressureShiftMeters: 0f,
        BallCapShiftMeters: 0f,
        RunShiftMeters: 0f,
        KeeperShiftMeters: 0f,
        DeepestRunnerId: 0,
        DeepestRunnerProjX: 0f,
        KeeperId: 0,
        KeeperReachMeters: 0f);

    private static DefensiveLineBand Evaluate(
        IReadOnlyList<FootballerState> players,
        float ballX,
        TeamBelief belief,
        BallPossessionSnapshot possession,
        float attackDirX = 1f,
        TeamTacticalProfile? profile = null) =>
        DefensiveLineModel.Evaluate(
            TeamId.Home, players, new BallState { Position = new Vector3(ballX, 0.11f, 0f) },
            possession, belief, profile ?? new TeamTacticalProfile(), attackDirX, Pitch);
}
