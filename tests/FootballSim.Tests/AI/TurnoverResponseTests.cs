using System.Collections.Generic;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Scenarios;
using FootballSim.Tactics;
using Xunit;

namespace FootballSim.Tests.AI;

/// <summary>
/// A6.4 at the team level: the exit gate's "secure and exposed turnover setups choose different transition
/// responses", and the answer actually reaches the bodies it is about.
///
/// The two fixtures are the same turnover — an away carrier with the ball at the halfway line, the same five
/// home outfielders, the same profiles — and differ only in where those five stand when the ball is lost: three
/// of them goal-side of it, or all five upfield of it. The model's own clauses live in
/// <see cref="TurnoverResponseModelTests"/>; this file proves the decision is consumed.
/// </summary>
public sealed class TurnoverResponseTests
{
    private static readonly PitchDimensions Pitch = new();

    private const int AwayCarrier = 201;
    private const int AwaySupport = 202;
    private const int AwayKeeper = 209;

    // ---------------------------------------------------------------------------------------------
    // The exit gate: secure versus exposed.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The same turnover with rest defence chooses <b>counter-press</b>; the same turnover with nothing
    /// behind the ball chooses <b>retreat</b>. This is the clause the milestone's exit gate names, and the
    /// two worlds differ in nothing but where the home side's five outfielders were standing.
    /// </summary>
    [Fact]
    public void TheSameTurnover_AnswersDifferentlyWhenTheRestDefenceDiffers()
    {
        TurnoverSituation secure = FirstTurnoverAnswer(secure: true);
        TurnoverSituation exposed = FirstTurnoverAnswer(secure: false);

        Assert.True(secure.Responds, "the secure turnover produced no transition at all");
        Assert.True(exposed.Responds, "the exposed turnover produced no transition at all");

        Assert.True(secure.Response == TurnoverResponse.CounterPress,
            $"the secure turnover answered {secure.Describe()}");
        Assert.True(exposed.Response == TurnoverResponse.Retreat,
            $"the exposed turnover answered {exposed.Describe()}");

        // Both worlds compare the same four inputs; only the picture differs.
        Assert.True(secure.BehindBall >= TurnoverResponseModel.MinBehindBallForCounterPress,
            $"the secure side had {secure.BehindBall} players behind the ball");
        Assert.True(exposed.BehindBall < TurnoverResponseModel.MinBehindBallForCounterPress,
            $"the exposed side still had {exposed.BehindBall} players behind the ball");
        Assert.True(secure.CentralSecurity01 > exposed.CentralSecurity01,
            $"security {secure.CentralSecurity01:F2} must beat {exposed.CentralSecurity01:F2}");
        Assert.True(secure.CounterPressScore > exposed.CounterPressScore,
            $"counter-press score {secure.CounterPressScore:F2} must beat {exposed.CounterPressScore:F2}");
        Assert.True(exposed.RetreatScore > secure.RetreatScore,
            $"retreat score {exposed.RetreatScore:F2} must beat {secure.RetreatScore:F2}");
    }

    /// <summary>
    /// The answer is one team fact: every defending player's observation carries the answer the allocator
    /// chose, and the band it placed them from reports the same shift — so a defender's press depth, his line
    /// and his team-mates' press cannot disagree about what the team is doing.
    /// </summary>
    [Fact]
    public void TheAnswer_ReachesTheLineAndEveryObservation()
    {
        (TurnoverSituation secureAnswer, List<int> secureSeen, List<float> secureShifts, int secureMarkers) =
            ObserveTurnoverAnswer(secure: true, TurnoverResponse.CounterPress);

        Assert.True(secureAnswer.Response == TurnoverResponse.CounterPress,
            $"the secure fixture answered {secureAnswer.Describe()}");
        Assert.True(secureShifts.Count > 0, "the secure fixture never produced a valid line band");
        foreach (float shift in secureShifts)
        {
            Assert.True(shift > 0f,
                $"a counter-press must squeeze the band up, not {shift:+0.0;-0.0} m");
        }

        (TurnoverSituation exposedAnswer, List<int> exposedSeen, List<float> exposedShifts, int exposedMarkers) =
            ObserveTurnoverAnswer(secure: false, TurnoverResponse.Retreat);

        Assert.True(exposedAnswer.Response == TurnoverResponse.Retreat,
            $"the exposed fixture answered {exposedAnswer.Describe()}");
        Assert.True(exposedShifts.Count > 0, "the exposed fixture never produced a valid line band");
        foreach (float shift in exposedShifts)
        {
            Assert.True(shift < 0f,
                $"a retreat must drop the band, not {shift:+0.0;-0.0} m");
        }

        // A6.3's zone/man mix gains a man when the team hunts the ball and loses one when it re-forms. The
        // retreating side may mark nobody at all here, because its five outfielders are upfield of the ball
        // and out of marking travel range — which is the retreat working, not the cap failing.
        Assert.True(secureMarkers > exposedMarkers,
            $"the counter-pressing side marked {secureMarkers} men and the retreating side {exposedMarkers}");
        Assert.True(secureMarkers >= 1, "the counter-pressing side marked nobody at all");
        Assert.Equal(1, secureAnswer.MarkerAdjustment);
        Assert.Equal(-1, exposedAnswer.MarkerAdjustment);

        // Every player's observation carries the allocator's answer, not a second opinion.
        Assert.True(secureSeen.Count > 0, "no home observation carried the counter-press");
        foreach (int observed in secureSeen)
            Assert.Equal((int)TurnoverResponse.CounterPress, observed);

        Assert.True(exposedSeen.Count > 0, "no home observation carried the retreat");
        foreach (int observed in exposedSeen)
            Assert.Equal((int)TurnoverResponse.Retreat, observed);
    }

    /// <summary>
    /// Two fresh engines running the same fixture answer identically on every sampled tick: the comparison
    /// consumes no randomness and no wall clock.
    /// </summary>
    [Fact]
    public void TheAnswer_IsDeterministic()
    {
        List<TurnoverSituation> first = SampledAnswers(secure: true);
        List<TurnoverSituation> second = SampledAnswers(secure: true);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i], second[i]);
    }

    // ---------------------------------------------------------------------------------------------
    // What the answer does to the bodies.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The presser's depth is the team answer first: contact range when the side has decided to win the ball
    /// back, a jockey when it is recovering, a shepherd when it is retreating. With no team answer behind the
    /// picture, the old rule (jockey when alone and bypassable) still decides — that is what keeps a
    /// standalone controller honest.
    /// </summary>
    [Fact]
    public void ThePressDepth_FollowsTheTeamAnswer()
    {
        Assert.Equal(PressPolicy.PressStandoffMeters, Standoff(TurnoverResponse.CounterPress));
        Assert.Equal(PressPolicy.DelayStandoffMeters, Standoff(TurnoverResponse.DelayRecover));
        Assert.Equal(PressPolicy.RetreatStandoffMeters, Standoff(TurnoverResponse.Retreat));

        // No team answer: an uncovered, bypassable press still jockeys.
        Assert.Equal(PressPolicy.DelayStandoffMeters, Standoff(TurnoverResponse.None));
        Assert.True(PressPolicy.RetreatStandoffMeters > PressPolicy.DelayStandoffMeters);
        Assert.True(PressPolicy.DelayStandoffMeters > PressPolicy.PressStandoffMeters);
    }

    /// <summary>
    /// A6.1's balance has one more input: a team that has decided to retreat does not add a second body in
    /// front of the ball, whatever the local picture says.
    /// </summary>
    [Fact]
    public void ARetreatingTeam_DoesNotCommitASecondMan()
    {
        PressSituation retreat = PressPolicy.BuildSituation(
            Observe(TurnoverResponse.Retreat, goalSide: true), Pitch);
        Assert.True(retreat.IsSupport);

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        int count = PressPolicy.Build(Observe(TurnoverResponse.Retreat, goalSide: true), retreat, Pitch, buffer);
        Assert.Equal(1, count);
        Assert.Equal(PressAction.Hold, buffer[0].Action);
        Assert.True(buffer[0].IsPrimary, "holding the ground is the retreating defender's decision for the tick");
    }

    /// <summary>
    /// The other side of it: a decided counter-press <b>licenses</b> the second man even where his own
    /// urgency would not, because the team already compared the numbers. The same picture with no answer
    /// behind it delays instead.
    /// </summary>
    [Fact]
    public void ACounterPress_LicensesTheSecondMan_WithoutLocalUrgency()
    {
        FootballerObservation pressing = Observe(TurnoverResponse.CounterPress, goalSide: true);
        PressSituation situation = PressPolicy.BuildSituation(pressing, Pitch);
        Assert.True(situation.IsSupport);
        Assert.True(situation.Urgency01 < PressPolicy.SupportUrgencyGate,
            $"the fixture must have low local urgency ({situation.Urgency01:F2}) for this to be about the team answer");

        Span<PressCandidate> buffer = stackalloc PressCandidate[PressPolicy.MaxCandidates];
        Assert.Equal(1, PressPolicy.Build(pressing, situation, Pitch, buffer));
        Assert.Equal(PressAction.SupportPress, buffer[0].Action);

        // The control: the identical picture with no team answer.
        FootballerObservation recovering = Observe(TurnoverResponse.None, goalSide: true);
        PressSituation plain = PressPolicy.BuildSituation(recovering, Pitch);
        Assert.Equal(1, PressPolicy.Build(recovering, plain, Pitch, buffer));
        Assert.Equal(PressAction.Delay, buffer[0].Action);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The turnover fixture: an away carrier holding the ball on the halfway line with one support runner, and
    /// five home outfielders. <paramref name="secure"/> places three of the five goal-side of the ball (the
    /// rest defence the turnover happens with); the exposed variant pushes all five upfield of it.
    /// </summary>
    private static MatchSimulationEngine EngineFor(bool secure)
    {
        var scenario = new MatchScenario
        {
            Name = secure ? "A6.4 turnover (secure)" : "A6.4 turnover (exposed)",
            Seed = 20261115UL,
            BallPosition = new Vector3(0f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = AwayCarrier,
            HomeProfile = TeamTacticalProfile.PossessionControl
        };

        scenario.Add(MatchScenario.Player(101, TeamId.Home, TacticalRole.Goalkeeper,
            new Vector3(-48f, 0f, 0f), 1));
        scenario.Add(MatchScenario.Player(102, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(secure ? -8f : 28f, 0f, -7f), 4));
        scenario.Add(MatchScenario.Player(103, TeamId.Home, TacticalRole.CenterBack,
            new Vector3(secure ? -8f : 28f, 0f, 7f), 5));
        scenario.Add(MatchScenario.Player(104, TeamId.Home, TacticalRole.DefensiveMidfielder,
            new Vector3(secure ? -4f : 32f, 0f, 0f), 6));
        scenario.Add(MatchScenario.Player(108, TeamId.Home, TacticalRole.CentralMidfielder,
            new Vector3(secure ? 4f : 34f, 0f, -8f), 8));
        scenario.Add(MatchScenario.Player(107, TeamId.Home, TacticalRole.Winger,
            new Vector3(secure ? 6f : 36f, 0f, 10f), 7));

        scenario.Add(MatchScenario.Player(AwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder,
            new Vector3(0f, 0f, 0f), 8).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "carrier holds the ball")));
        scenario.Add(MatchScenario.Player(AwaySupport, TeamId.Away, TacticalRole.Striker,
            new Vector3(10f, 0f, 8f), 9).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "support holds")));

        // Two away strikers already goal-side of the ball, in both worlds: the marking mix needs a threat to
        // pick up, and they are the same in the secure and the exposed fixture.
        scenario.Add(MatchScenario.Player(203, TeamId.Away, TacticalRole.Striker,
            new Vector3(-24f, 0f, -8f), 10).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "striker holds")));
        scenario.Add(MatchScenario.Player(204, TeamId.Away, TacticalRole.Striker,
            new Vector3(-24f, 0f, 8f), 11).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "striker holds")));

        scenario.Add(MatchScenario.Player(AwayKeeper, TeamId.Away, TacticalRole.Goalkeeper,
            new Vector3(46f, 0f, 0f), 1).WithController(
            new FixedCommandController(ScenarioCommands.Hold(), "keeper holds")));

        return new MatchSimulationEngine(scenario);
    }

    /// <summary>The answer the allocator publishes at the first defending pass.</summary>
    private static TurnoverSituation FirstTurnoverAnswer(bool secure)
    {
        MatchSimulationEngine engine = EngineFor(secure);
        for (int tick = 0; tick < 3; tick++) engine.StepTick();
        return engine.TeamJobs.TurnoverOf(TeamId.Home);
    }

    private static List<TurnoverSituation> SampledAnswers(bool secure)
    {
        MatchSimulationEngine engine = EngineFor(secure);
        var answers = new List<TurnoverSituation>();
        for (int tick = 1; tick <= 120; tick++)
        {
            engine.StepTick();
            answers.Add(engine.TeamJobs.TurnoverOf(TeamId.Home));
        }

        return answers;
    }

    /// <summary>
    /// Samples the published answer, the band's turnover shift and the marking count while the fixture holds
    /// its <paramref name="expected"/> answer. Also checks that every home player's observation carries the
    /// team's answer.
    /// </summary>
    private static (TurnoverSituation Answer, List<int> Seen, List<float> Shifts, int Markers)
        ObserveTurnoverAnswer(bool secure, TurnoverResponse expected)
    {
        MatchSimulationEngine engine = EngineFor(secure);
        var seen = new List<int>();
        var shifts = new List<float>();

        TurnoverSituation answer = TurnoverSituation.None;
        int markers = 0;

        for (int tick = 1; tick <= 40; tick++)
        {
            engine.StepTick();

            TurnoverSituation current = engine.TeamJobs.TurnoverOf(TeamId.Home);
            if (current.Response != expected) continue;
            if (answer.Response == TurnoverResponse.None) answer = current;

            DefensiveLineBand band = engine.TeamJobs.LineBandOf(TeamId.Home);
            if (band.IsValid) shifts.Add(band.TurnoverShiftMeters);

            int marked = 0;
            foreach (FootballerState player in engine.Players)
            {
                if (player.Team != TeamId.Home || !player.IsActiveOnPitch) continue;
                if (engine.TeamJobs.Get(player.Id)?.Job == TacticalJob.Marker) marked++;

                FootballerObservation? observation = engine.GetLastObservation(player.Id);
                if (observation is null) continue;
                seen.Add((int)observation.TeamTurnover.Response);
            }

            if (marked > markers) markers = marked;
        }

        return (answer, seen, shifts, markers);
    }

    // ---------------------------------------------------------------------------------------------
    // Observation-level fixtures for the press rules.
    // ---------------------------------------------------------------------------------------------

    private const int SelfId = 4;
    private const int PresserId = 6;
    private const int CarrierId = 15;
    private const int MateId = 7;

    /// <summary>
    /// A defender six metres goal-side of the away carrier, with the team's elected presser beside him and no
    /// cover behind the press. The carrier is just outside this side's own half (X = -8), so the
    /// "inside our own half" clause cannot make engaging worthwhile on its own: the fixture is deliberately
    /// the picture in which the local urgency test says "delay".
    /// </summary>
    private static FootballerObservation Observe(TurnoverResponse response, bool goalSide)
    {
        var visible = new List<PerceivedEntity>
        {
            new(CarrierId, TeamId.Away, TacticalRole.Winger,
                new Vector3(-8f, 0f, 0f), Vector3.Zero, 1f),
            new(PresserId, TeamId.Home, TacticalRole.DefensiveMidfielder,
                new Vector3(-7f, 0f, 2f), Vector3.Zero, 1f),
            // Upfield of the presser on purpose: nobody is goal-side of the press, so the local picture is
            // exactly the one the "unsupported press can lose to delay" rule is for.
            new(MateId, TeamId.Home, TacticalRole.CenterBack,
                new Vector3(-2f, 0f, -12f), Vector3.Zero, 1f)
        };

        return new FootballerObservation
        {
            SelfId = SelfId,
            SelfTeam = TeamId.Home,
            SelfRole = TacticalRole.CenterBack,
            SelfPosition = new Vector3(goalSide ? -13f : -3f, 0f, 4f),
            SelfVelocity = Vector3.Zero,
            SelfFacingXZ = new Vector2(1f, 0f),
            Stamina01 = 1f,
            AvailableToKick = true,
            ControlQuality01 = 0f,
            IsSetPieceTaker = false,
            PerceivedBallPosition = new Vector3(-8.3f, 0f, 0f),
            PerceivedBallVelocity = Vector3.Zero,
            PossessingTeam = TeamId.Away,
            BallLastTouchedByPlayerId = CarrierId,
            BallLastTouchedByTeam = TeamId.Away,
            BallControlState = BallControlState.Controlled,
            BallControllingPlayerId = CarrierId,
            TacticalAnchorPosition = new Vector3(goalSide ? -13f : -3f, 0f, 4f),
            TacticalJob = TacticalJob.Shape,
            AttackingDirectionX = 1f,
            MatchTimeSeconds = 30f,
            CurrentMatchPhase = MatchPhase.FirstHalf,
            Tick = 900,
            IsDesignatedBallResponder = false,
            BallResponderPlayerId = PresserId,
            SupportPresserPlayerId = SelfId,
            VisiblePlayers = visible,
            TeamProfile = TeamTacticalProfile.PossessionControl,
            SelfTendencies = PlayerTendencyProfile.ForRole(TacticalRole.CenterBack),
            SelfAttributes = new PlayerAttributes(),
            TeamTurnover = TurnoverSituation.None with { Response = response }
        };
    }

    private static float Standoff(TurnoverResponse response)
    {
        PressSituation situation = PressPolicy.BuildSituation(Observe(response, goalSide: true), Pitch);
        Assert.True(situation.HasCarrier, "the fixture's carrier is not in the picture");
        return PressPolicy.PressStandoffFor(situation);
    }
}
