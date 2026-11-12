using System;
using System.Collections.Generic;
using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Scenarios;

/// <summary>
/// The ten fixture families the plan requires (A0.3), plus the shared helpers that build them.
///
/// These live in the simulation assembly rather than the test assembly because they are the documented
/// contract of what the AI must handle: the headless CLI and the xUnit suite run the same fragment, and
/// a fixture cannot drift between them. They are Godot-free by construction.
///
/// Conventions, so a reader can orient without a pitch diagram:
/// <list type="bullet">
/// <item>Home attacks +X; away attacks −X. The home goal is at X = −52.5, the away goal at X = +52.5.</item>
/// <item>Player ids are 1xx for home and 2xx for away. Shirts follow the 11-a-side table in
/// <see cref="Tactics.TeamTacticalCoordinator.GetFormationRoleAndSlot"/> (1 GK, 2/3 FB, 4/5 CB, 6 DM,
/// 7/11 W, 8/10 CM, 9 ST), because the current anchor model is keyed by shirt number.</item>
/// <item>The ball rests at the ball radius above the grass when a fixture does not lift it explicitly.</item>
/// </list>
///
/// Each fixture is deliberately small (2–6 players a side). A scenario is a question about one
/// behaviour; eleven players would make every answer a team-level average.
/// </summary>
public static class ScenarioLibrary
{
    /// <summary>One named fixture: a factory rather than a shared instance, because controllers are stateful.</summary>
    public sealed record Definition(string Key, string Intent, Func<MatchScenario> Build);

    public static readonly IReadOnlyList<Definition> All = new Definition[]
    {
        new("build-up-3v2", "A carrier under a 3v2 has useful short and deeper options; a free receiver can be found.",
            BuildUpThreeVersusTwo),
        new("wide-overload-4v3", "Complementary width and support turn a wide overload into an opportunity instead of congestion.",
            WideOverload),
        new("cb-step-out", "A centre-back leaving the line is covered by the right player, who keeps the gap until handback is safe.",
            CenterBackStepsOut),
        new("rear-pressure-reception", "A receiver with pressure from behind chooses shield, bounce, turn or touch from knowledge, not truth.",
            RearPressureReception),
        new("high-line-run", "Two attackers against a high line: timing before release, contact-snapshot eligibility after it.",
            HighLineRun),
        new("winger-fullback", "The overlap/underlap/support choice follows the defender's response and the rest-defence cost.",
            WingerFullbackOverlap),
        new("counterattack-3v3", "A forward opportunity is weighed against pressure and loss exposure; the defence can delay or recover.",
            CounterattackThreeVersusThree),
        new("touchline-press", "Angled pressure plus a screened escape and useful cover; a sensible exit when the press is bypassed.",
            TouchlinePress),
        new("box-cross", "Complementary box arrivals, a genuinely deliverable trajectory, and recycling when delivery is poor.",
            BoxCross),
        new("keeper-sweep-race", "Ball height, competing arrival, defender recovery and goal exposure decide the sweep.",
            KeeperSweepRace)
    };

    // ---- 1. 3v2 build-up -------------------------------------------------------------------

    public const int BuildUpHomeGk = 101;
    public const int BuildUpHomeCb = 102;
    public const int BuildUpHomeCm = 103;
    public const int BuildUpAwayCb = 201;
    public const int BuildUpAwayCm = 202;

    /// <summary>Home builds from a central midfielder with a centre-back and keeper behind him.</summary>
    public static MatchScenario BuildUpThreeVersusTwo()
    {
        var scenario = new MatchScenario
        {
            Name = "3v2 build-up",
            Seed = 4242UL,
            BallPosition = new Vector3(-4.4f, 0f, 8f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = BuildUpHomeCm
        };

        scenario.Add(Player(BuildUpHomeGk, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(BuildUpHomeCb, TeamId.Home, TacticalRole.CenterBack, -20f, -4f, 4));
        scenario.Add(Player(BuildUpHomeCm, TeamId.Home, TacticalRole.CentralMidfielder, -5f, 8f, 8));
        scenario.Add(Player(BuildUpAwayCb, TeamId.Away, TacticalRole.CenterBack, 10f, 0f, 4));
        scenario.Add(Player(BuildUpAwayCm, TeamId.Away, TacticalRole.CentralMidfielder, 2f, -8f, 8));
        return scenario;
    }

    // ---- 2. 4v3 wide overload --------------------------------------------------------------

    public const int WideHomeFb = 104;
    public const int WideHomeWinger = 107;

    /// <summary>Home overloads the right touchline; the far-side winger holds width opposite the ball.</summary>
    public static MatchScenario WideOverload()
    {
        var scenario = new MatchScenario
        {
            Name = "4v3 wide overload",
            Seed = 5151UL,
            BallPosition = new Vector3(-7.3f, 0f, 26.4f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = WideHomeFb
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -16f, 0f, 4));
        scenario.Add(Player(106, TeamId.Home, TacticalRole.CentralMidfielder, -4f, 10f, 8));
        scenario.Add(Player(WideHomeFb, TeamId.Home, TacticalRole.FullBack, -8f, 26f, 2));
        scenario.Add(Player(WideHomeWinger, TeamId.Home, TacticalRole.Winger, 4f, 30f, 7));
        scenario.Add(Player(201, TeamId.Away, TacticalRole.CenterBack, 8f, 4f, 4));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CentralMidfielder, 0f, -2f, 8));
        scenario.Add(Player(203, TeamId.Away, TacticalRole.FullBack, 10f, 24f, 2));
        return scenario;
    }

    // ---- 3. centre-back steps out ----------------------------------------------------------

    public const int StepOutHomeCb = 102;
    public const int StepOutAwayCarrier = 201;

    /// <summary>The away carrier is allowed to receive between the lines; a centre-back must leave the back four.</summary>
    public static MatchScenario CenterBackStepsOut()
    {
        var scenario = new MatchScenario
        {
            Name = "CB steps out",
            Seed = 6262UL,
            BallPosition = new Vector3(-5.4f, 0f, -9.8f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = StepOutAwayCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(StepOutHomeCb, TeamId.Home, TacticalRole.CenterBack, -20f, -8f, 4));
        scenario.Add(Player(103, TeamId.Home, TacticalRole.CenterBack, -22f, 6f, 5));
        scenario.Add(Player(106, TeamId.Home, TacticalRole.DefensiveMidfielder, -14f, -2f, 6));
        scenario.Add(Player(StepOutAwayCarrier, TeamId.Away, TacticalRole.CentralMidfielder, -6f, -10f, 8));
        scenario.Add(Player(209, TeamId.Away, TacticalRole.Striker, -8f, 2f, 9));
        scenario.Add(Player(207, TeamId.Away, TacticalRole.Winger, 6f, 26f, 7));
        return scenario;
    }

    // ---- 4. reception under rear pressure --------------------------------------------------

    public const int RearPressurePasser = 102;
    public const int RearPressureReceiver = 106;
    public const int RearPressurePresser = 205;

    /// <summary>
    /// A pass is already travelling from the centre-back to the defensive midfielder, and the presser
    /// closes from behind the receiver — the side the receiver cannot see while facing the passer.
    /// The ball is in flight, not controlled: the receiver's first touch is the decision under test.
    /// </summary>
    public static MatchScenario RearPressureReception()
    {
        var scenario = new MatchScenario
        {
            Name = "rear-pressure reception",
            Seed = 7373UL,
            BallPosition = new Vector3(-14f, 0.9f, 4f),
            BallVelocity = new Vector3(2.2f, 0f, 0f),
            BallSetup = ScenarioBallSetup.InFlightPass,
            BallOwnerPlayerId = RearPressurePasser,
            IntendedReceiverId = RearPressureReceiver
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -46f, 0f, 1));
        scenario.Add(Player(RearPressurePasser, TeamId.Home, TacticalRole.CenterBack, -30f, 6f, 4));
        scenario.Add(Player(RearPressureReceiver, TeamId.Home, TacticalRole.DefensiveMidfielder, -12f, 4f, 6));
        scenario.Add(Player(RearPressurePresser, TeamId.Away, TacticalRole.DefensiveMidfielder, -9.6f, 4.2f, 6));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CentralMidfielder, -6f, -6f, 8));
        return scenario;
    }

    // ---- 5. high line ----------------------------------------------------------------------

    public const int HighLineStriker = 109;
    public const int HighLineCarrier = 108;

    /// <summary>
    /// The away line sits at X = −5, ten metres beyond the halfway line from their own goal: high by
    /// any standard. The home striker is onside (level with the second-last defender and behind the
    /// ball) and can attack the space in behind.
    /// </summary>
    public static MatchScenario HighLineRun()
    {
        var scenario = new MatchScenario
        {
            Name = "high-line run",
            Seed = 8484UL,
            BallPosition = new Vector3(-19.4f, 0f, 14f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = HighLineCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -25f, -10f, 4));
        scenario.Add(Player(111, TeamId.Home, TacticalRole.Winger, -15f, -24f, 11));
        scenario.Add(Player(HighLineCarrier, TeamId.Home, TacticalRole.CentralMidfielder, -20f, 14f, 8));
        scenario.Add(Player(HighLineStriker, TeamId.Home, TacticalRole.Striker, -8f, 0f, 9));
        scenario.Add(Player(201, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f, 1));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CenterBack, -5f, -6f, 4));
        scenario.Add(Player(203, TeamId.Away, TacticalRole.CenterBack, -5f, 6f, 5));
        scenario.Add(Player(204, TeamId.Away, TacticalRole.FullBack, -8f, -22f, 2));
        return scenario;
    }

    // ---- 6. winger and full-back -----------------------------------------------------------

    public const int WingerCarrier = 107;
    public const int OverlapFullBack = 104;

    /// <summary>The winger has the ball high and wide; the full-back is behind him and can overlap.</summary>
    public static MatchScenario WingerFullbackOverlap()
    {
        var scenario = new MatchScenario
        {
            Name = "winger/fullback overlap",
            Seed = 9595UL,
            BallPosition = new Vector3(10.6f, 0f, 26f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = WingerCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -18f, 0f, 4));
        scenario.Add(Player(106, TeamId.Home, TacticalRole.CentralMidfielder, 0f, 10f, 8));
        scenario.Add(Player(OverlapFullBack, TeamId.Home, TacticalRole.FullBack, 0f, 22f, 2));
        scenario.Add(Player(WingerCarrier, TeamId.Home, TacticalRole.Winger, 10f, 26f, 7));
        scenario.Add(Player(201, TeamId.Away, TacticalRole.CenterBack, 16f, 8f, 4));
        scenario.Add(Player(203, TeamId.Away, TacticalRole.FullBack, 14f, 28f, 2));
        scenario.Add(Player(206, TeamId.Away, TacticalRole.DefensiveMidfielder, 8f, 12f, 6));
        return scenario;
    }

    // ---- 7. counterattack ------------------------------------------------------------------

    public const int CounterCarrier = 102;

    /// <summary>Home has just won the ball deep; three attackers face three recovering defenders.</summary>
    public static MatchScenario CounterattackThreeVersusThree()
    {
        var scenario = new MatchScenario
        {
            Name = "3v3 counterattack",
            Seed = 10606UL,
            BallPosition = new Vector3(-1.4f, 0f, 0f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = CounterCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -40f, 0f, 1));
        scenario.Add(Player(CounterCarrier, TeamId.Home, TacticalRole.CenterBack, -2f, 0f, 4));
        scenario.Add(Player(109, TeamId.Home, TacticalRole.Striker, 8f, -6f, 9));
        scenario.Add(Player(111, TeamId.Home, TacticalRole.Winger, 6f, 14f, 11));
        scenario.Add(Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f, 1));
        scenario.Add(Player(201, TeamId.Away, TacticalRole.CenterBack, 20f, 0f, 4));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CenterBack, 18f, 10f, 5));
        scenario.Add(Player(206, TeamId.Away, TacticalRole.DefensiveMidfielder, 10f, -4f, 6));
        return scenario;
    }

    // ---- 8. touchline press ----------------------------------------------------------------

    public const int TouchlineCarrier = 207;

    /// <summary>An away winger is trapped on the touchline; home presses from the inside and screens the exit.</summary>
    public static MatchScenario TouchlinePress()
    {
        var scenario = new MatchScenario
        {
            Name = "touchline press",
            Seed = 11717UL,
            BallPosition = new Vector3(0.6f, 0f, -30f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = TouchlineCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -46f, 0f, 1));
        scenario.Add(Player(104, TeamId.Home, TacticalRole.FullBack, -6f, -28f, 2));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -12f, -18f, 4));
        scenario.Add(Player(106, TeamId.Home, TacticalRole.DefensiveMidfielder, -4f, -12f, 6));
        scenario.Add(Player(209, TeamId.Away, TacticalRole.Goalkeeper, 46f, 0f, 1));
        scenario.Add(Player(TouchlineCarrier, TeamId.Away, TacticalRole.Winger, 0f, -30f, 7));
        scenario.Add(Player(208, TeamId.Away, TacticalRole.CentralMidfielder, -4f, -24f, 8));
        return scenario;
    }

    // ---- 9. box cross ----------------------------------------------------------------------

    public const int CrossCarrier = 107;

    /// <summary>The winger reaches the byline with three complementary arrivals and four defenders in the box.</summary>
    public static MatchScenario BoxCross()
    {
        var scenario = new MatchScenario
        {
            Name = "box cross",
            Seed = 12818UL,
            BallPosition = new Vector3(30.6f, 0f, 26f),
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = CrossCarrier
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -48f, 0f, 1));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -20f, 0f, 4));
        scenario.Add(Player(104, TeamId.Home, TacticalRole.FullBack, 34f, -12f, 2));
        scenario.Add(Player(108, TeamId.Home, TacticalRole.CentralMidfielder, 38f, 10f, 8));
        scenario.Add(Player(109, TeamId.Home, TacticalRole.Striker, 40f, 2f, 9));
        scenario.Add(Player(CrossCarrier, TeamId.Home, TacticalRole.Winger, 30f, 26f, 7));
        scenario.Add(Player(201, TeamId.Away, TacticalRole.Goalkeeper, 50f, 0f, 1));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CenterBack, 43f, -4f, 4));
        scenario.Add(Player(203, TeamId.Away, TacticalRole.CenterBack, 44f, 8f, 5));
        scenario.Add(Player(204, TeamId.Away, TacticalRole.FullBack, 46f, 20f, 2));
        return scenario;
    }

    // ---- 10. keeper sweep race -------------------------------------------------------------

    public const int SweepKeeper = 201;
    public const int SweepChaser = 109;

    /// <summary>
    /// The ball is rolling into the space behind a high away line. The keeper is 8 m outside his goal,
    /// the centre-back is recovering, and the striker is arriving from the other side of the ball.
    /// </summary>
    public static MatchScenario KeeperSweepRace()
    {
        var scenario = new MatchScenario
        {
            Name = "keeper sweep race",
            Seed = 13919UL,
            BallPosition = new Vector3(30f, 0f, -6f),
            BallVelocity = new Vector3(5f, 0f, -0.4f),
            BallSetup = ScenarioBallSetup.Loose
        };

        scenario.Add(Player(101, TeamId.Home, TacticalRole.Goalkeeper, -46f, 0f, 1));
        scenario.Add(Player(102, TeamId.Home, TacticalRole.CenterBack, -10f, 0f, 4));
        scenario.Add(Player(SweepChaser, TeamId.Home, TacticalRole.Striker, 24f, 6f, 9));
        scenario.Add(Player(SweepKeeper, TeamId.Away, TacticalRole.Goalkeeper, 44f, 0f, 1));
        scenario.Add(Player(202, TeamId.Away, TacticalRole.CenterBack, 34f, 6f, 4));
        return scenario;
    }

    /// <summary>Builds a player with the neutral attributes and the role's tendency preset.</summary>
    private static ScenarioPlayer Player(int id, TeamId team, TacticalRole role, float x, float z, int shirt) =>
        MatchScenario.Player(id, team, role, new Vector3(x, 0f, z), shirt);
}
