using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Scenarios;
using Xunit;

namespace FootballSim.Tests;

/// <summary>
/// Regressions for AUD-13c of docs/Bug_Audit_2026-11-22.md: <c>MatchEngine.StepTick</c> used to publish a
/// player's team call inside the same per-player pass that built the observations. At the zero-delay
/// ablation (<c>TeamCallCommunicationDelayTicks = 0</c>) a shout was therefore audible inside its own tick
/// to every player registered after its caller, but only on the next tick to players registered before
/// him — a reception order that depended on player id/registration order rather than on the game.
///
/// The fix builds every observation for the tick first and publishes the tick's calls only afterwards, so a
/// call published on tick T is first heard on tick T+1 by every team-mate, whichever id direction it
/// travels. The shipped default (<c>TeamCallCommunicationDelayTicks = 18</c>) is guarded separately and
/// deliberately at full-match scale: moving the publication out of the loop must not change a match that
/// uses the shipped delay.
///
/// Every test states why the pre-fix code fails it, and why it passes after the fix.
/// </summary>
public sealed class AudFixCallOrderTests
{
    /// <summary>The carrier's id sits after the ids that shout "turn" at him, so the pre-fix loop built his
    /// observation after their publications and made the shout audible on its own publish tick.</summary>
    private const int TurnProbeCarrierId = 105;

    /// <summary>The away probe's carrier is registered before its shouters, i.e. the id direction the
    /// pre-fix loop already delivered a tick late; both directions must now behave alike.</summary>
    private const int TurnProbeAwayCarrierId = 201;

    /// <summary>
    /// AUD-13c: at delay 0 a call published on tick T must not be audible to any tick-T observation and
    /// must be heard on tick T+1. This is asserted through the shipped <see cref="TeamCallPlanner"/>, in both
    /// id directions, with the shouters registered before and after their target.
    ///
    /// Pre-fix failure (home direction): the loop built the shouters' observations and published their
    /// "turn" shouts before it built the carrier's observation, so player #105's tick-1 observation carried
    /// a Turn call. The away direction (caller registered after the target) already waited for tick 2; the
    /// point of running both is that after the fix they are equal.
    /// </summary>
    [Fact]
    public void ZeroDelayShoutsArriveOnTheNextTickInBothIdDirections()
    {
        AssertShoutArrivesOnTheNextTick(TurnShoutScenario(), TeamId.Home, TurnProbeCarrierId, 101);
        AssertShoutArrivesOnTheNextTick(AwayTurnShoutScenario(), TeamId.Away, TurnProbeAwayCarrierId, 202);
    }

    /// <summary>
    /// AUD-13c property over a short live passage: with the shipped call planner and delay 0, no call the
    /// board accepts on tick T may be present in any observation built on tick T.
    ///
    /// Pre-fix failure: at tick 1 the team-mates registered before the carrier shout "turn" at him and the
    /// old loop published those shouts before the carrier's observation was built, so his observation
    /// carried one on its publish tick. The assertion is over every player and every tick, so it also
    /// catches any other same-tick call leak.
    /// </summary>
    [Fact]
    public void ZeroDelayTeamCallIsNeverAudibleOnItsPublishTick()
    {
        var engine = new MatchSimulationEngine(TurnShoutScenario());
        TeamCallBoard board = engine.Communication.Calls;
        bool sawPublishedCall = false;

        for (int tick = 0; tick < 60; tick++)
        {
            engine.StepTick();

            for (int index = 0; index < engine.Players.Count; index++)
            {
                FootballerState player = engine.Players[index];
                if (!player.IsActiveOnPitch) continue;

                FootballerObservation? observation = engine.GetLastObservation(player.Id);

                // A suspended tick builds no new observation; nothing was decided (or published) on it.
                if (observation is null || observation.Tick != engine.CurrentTick) continue;

                if (!board.TryGetPlayerCall(player.Team, player.Id, engine.CurrentTick, out TeamCall addressed))
                    continue;
                if (addressed.PublishedTick != engine.CurrentTick) continue;

                sawPublishedCall = true;

                // A board accepts at most one live call of one shape (team, kind, caller, target), so an
                // observation that shows this kind from this caller can only be hearing this call.
                bool heardOnPublishTick = observation.Calls.HasPlayerCall &&
                                          observation.Calls.Kind == addressed.Kind &&
                                          observation.Calls.CallerId == addressed.CallerId;
                Assert.False(
                    heardOnPublishTick,
                    $"tick {engine.CurrentTick}: player #{player.Id} heard #{addressed.CallerId}'s " +
                    $"{addressed.Kind} in the observation built on the call's publish tick (AUD-13c)");
            }
        }

        Assert.True(
            sawPublishedCall,
            "fixture produced no team call within 60 ticks at delay 0; the assertion above would be vacuous");
    }

    /// <summary>
    /// AUD-13c guard for the shipped default: moving the team-call publication out of the observation pass
    /// must not change a match that uses the shipped 18-tick communication delay. At a delay of at least one
    /// tick a same-tick call was never usable, so the whole shipped match must be bit-for-bit unchanged.
    ///
    /// The fixture is deliberately the shipped eleven-a-side pre-match configuration rather than a small
    /// fragment: it runs long enough for a decision to end a live combination contract (the first
    /// <see cref="CombinationEndReason.AbandonedByReceiver"/> event in this fixture happens at tick 1213),
    /// which is the one cross-player board mutation whose same-tick visibility an "observations first,
    /// decisions second" restructuring would change. This test is the guard that caught exactly that
    /// regression while the fix was under review; the fingerprint covers positions, analytics, and the call
    /// and combination board counters, including contract-end reasons.
    ///
    /// Pre-fix failure: none — this is the "did not change" guard, deliberately capturing the state the fix
    /// must preserve. It fails if the shipped-delay path ever starts behaving differently.
    ///
    /// <para><b>ai55 re-capture:</b> the AIB-01..AIB-06 behaviour fixes (carrier pressure, pass lanes and
    /// leads, box support, central penetration, shot valuation, backward passes) legitimately move the shipped
    /// default match, so the fingerprint was re-captured at <c>ai55-rs1</c>. The AUD-13c property is still
    /// guarded by the zero-delay tests above; this test remains a drift guard for the shipped path, not a claim
    /// that AI changes never move a match.</para>
    /// </summary>
    [Fact]
    public void ShippedDefaultDelayKeepsTheFixedScenarioUnchanged()
    {
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 20260818UL,
            PlayersPerTeam = 11
        });

        for (int tick = 0; tick < 1500; tick++)
            engine.StepTick();

        string fingerprint = Fingerprint(engine);
        Assert.Equal(ExpectedDefaultScenarioFingerprint, fingerprint);
    }

    private static void AssertShoutArrivesOnTheNextTick(
        MatchScenario scenario, TeamId shoutingTeam, int carrierId, int expectedCallerId)
    {
        var engine = new MatchSimulationEngine(scenario);
        TeamCallBoard board = engine.Communication.Calls;

        engine.StepTick();

        // The shipped planner accepted at least one Turn shout aimed at the carrier on tick 1 and, at delay
        // 0, the board says it is usable on tick 1 — but the observation built on tick 1 must not carry it.
        Assert.True(board.TryGetPlayerCall(shoutingTeam, carrierId, engine.CurrentTick, out TeamCall published));
        Assert.Equal(1, published.PublishedTick);
        Assert.Equal(TeamCallKind.Turn, published.Kind);
        Assert.Equal(carrierId, published.TargetPlayerId);
        Assert.False(Observation(engine, carrierId).Calls.HasPlayerCall);

        engine.StepTick();

        // Exactly one tick later it arrives, and the board's answer is the same lowest-slot shout in both
        // registration directions.
        TeamCallContext heard = Observation(engine, carrierId).Calls;
        Assert.True(heard.HasPlayerCall);
        Assert.Equal(TeamCallKind.Turn, heard.Kind);
        Assert.Equal(expectedCallerId, heard.CallerId);
    }

    /// <summary>
    /// A home carrier (id 105, registered after his shouters) with the ball and no opponent inside the
    /// 5.5 m "turn" free radius, so his earlier-registered team-mates shout "turn" at him on tick 1. Every
    /// player is AI-controlled: this fixture exercises the engine's own publication, not a probe.
    /// </summary>
    private static MatchScenario TurnShoutScenario()
    {
        var scenario = new MatchScenario
        {
            Name = "aud13c turn-shout probe (low-id caller)",
            Seed = 20261122UL,
            Ai = new AiRuntimeConfig { TeamCallCommunicationDelayTicks = 0 },
            BallPosition = Vector3.Zero,
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = TurnProbeCarrierId
        };

        scenario.Add(MatchScenario.Player(
            101, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-8f, 0f, 0f), 8));
        scenario.Add(MatchScenario.Player(
            102, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-8f, 0f, 6f), 10));
        scenario.Add(MatchScenario.Player(
            103, TeamId.Home, TacticalRole.CenterBack, new Vector3(-14f, 0f, -6f), 4));
        scenario.Add(MatchScenario.Player(
            104, TeamId.Home, TacticalRole.Striker, new Vector3(-4f, 0f, 14f), 9));
        scenario.Add(MatchScenario.Player(
            TurnProbeCarrierId, TeamId.Home, TacticalRole.CentralMidfielder, Vector3.Zero, 6));
        scenario.Add(MatchScenario.Player(
            205, TeamId.Away, TacticalRole.CenterBack, new Vector3(30f, 0f, 0f), 4));
        scenario.Add(MatchScenario.Player(
            206, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(32f, 0f, -10f), 8));
        return scenario;
    }

    /// <summary>
    /// The mirror image: an away carrier registered before his shouters, so the shout travels from a higher
    /// registration index to a lower one. His team-mates are the ones inside the "turn" sight range; the
    /// home players stay far away so nobody presses the carrier.
    /// </summary>
    private static MatchScenario AwayTurnShoutScenario()
    {
        var scenario = new MatchScenario
        {
            Name = "aud13c turn-shout probe (high-id caller)",
            Seed = 20261123UL,
            Ai = new AiRuntimeConfig { TeamCallCommunicationDelayTicks = 0 },
            BallPosition = Vector3.Zero,
            BallSetup = ScenarioBallSetup.Controlled,
            BallOwnerPlayerId = TurnProbeAwayCarrierId
        };

        scenario.Add(MatchScenario.Player(
            TurnProbeAwayCarrierId, TeamId.Away, TacticalRole.CentralMidfielder, Vector3.Zero, 8));
        scenario.Add(MatchScenario.Player(
            202, TeamId.Away, TacticalRole.CentralMidfielder, new Vector3(8f, 0f, 0f), 10));
        scenario.Add(MatchScenario.Player(
            203, TeamId.Away, TacticalRole.CenterBack, new Vector3(14f, 0f, -6f), 4));
        scenario.Add(MatchScenario.Player(
            101, TeamId.Home, TacticalRole.CenterBack, new Vector3(-30f, 0f, 0f), 4));
        scenario.Add(MatchScenario.Player(
            102, TeamId.Home, TacticalRole.CentralMidfielder, new Vector3(-32f, 0f, 10f), 8));
        return scenario;
    }

    private static FootballerObservation Observation(MatchSimulationEngine engine, int playerId) =>
        engine.GetLastObservation(playerId)
        ?? throw new InvalidOperationException($"No observation was built for player #{playerId}.");

    /// <summary>
    /// Round-trips every state value that the publication move could affect: authoritative positions, the
    /// analytics the execution layer recorded, and the call/combination board counters (including how
    /// combination contracts ended). Floats use the round-trip format so an equality failure is a real
    /// behavioural difference, not a formatting artefact.
    /// </summary>
    private static string Fingerprint(MatchSimulationEngine engine)
    {
        var text = new StringBuilder();
        text.Append("tick=").Append(engine.CurrentTick);
        text.Append("|ball=").Append(Float(engine.Ball.Position.X)).Append(',')
            .Append(Float(engine.Ball.Position.Z));

        text.Append("|players=");
        for (int index = 0; index < engine.Players.Count; index++)
        {
            FootballerState player = engine.Players[index];
            text.Append(player.Id).Append(':')
                .Append(Float(player.Position.X)).Append(',')
                .Append(Float(player.Position.Z)).Append(';');
        }

        MatchAnalyticsSummary analytics = engine.Analytics;
        text.Append("|analytics=")
            .Append(analytics.HomePossessionTicks).Append(',')
            .Append(analytics.AwayPossessionTicks).Append(',')
            .Append(analytics.HomeControlledPossessionTicks).Append(',')
            .Append(analytics.AwayControlledPossessionTicks).Append(',')
            .Append(analytics.HomePassesAttempted).Append(',')
            .Append(analytics.AwayPassesAttempted).Append(',')
            .Append(analytics.HomePassesCompleted).Append(',')
            .Append(analytics.AwayPassesCompleted).Append(',')
            .Append(analytics.HomeShots).Append(',')
            .Append(analytics.AwayShots).Append(',')
            .Append(analytics.HomeGoals).Append(',')
            .Append(analytics.AwayGoals);

        TeamCallBoard calls = engine.Communication.Calls;
        text.Append("|calls=")
            .Append(calls.PublishedCount).Append(',')
            .Append(calls.SuppressedCount).Append(',')
            .Append(calls.ExpiredCount).Append(',')
            .Append(calls.EvictedCount);

        CombinationCoordinator combinations = engine.Communication.Combinations;
        text.Append("|combinations=")
            .Append(combinations.PublishedCount).Append(',')
            .Append(combinations.ContinuedCount).Append(',')
            .Append(combinations.SuppressedCount).Append(',')
            .Append(combinations.EvictedCount).Append(',')
            .Append(combinations.CountOfEnd(CombinationEndReason.AbandonedByReceiver)).Append(',')
            .Append(combinations.CountOfEnd(CombinationEndReason.Replaced)).Append(',')
            .Append(combinations.CountOfEnd(CombinationEndReason.PossessionEnded));

        return text.ToString();
    }

    private static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Captured on the pre-fix engine (commit 4ed6aab, before the AUD-13c publication move) at <c>ai53</c>,
    /// re-captured at <c>ai55-rs1</c> after the AIB-01..AIB-06 behaviour fixes, at <c>ai56-rs1</c> after the
    /// 2026-11-23 passing/space audit fixes (AUD-P1..AUD-P7) and again at <c>ai57-rs1</c> after the AUD-P7
    /// review correction (the near-line power cap replaced by a priced boundary-exit risk). The shipped 18-tick
    /// delay means no same-tick call was ever usable, so the fix must reproduce the same path *for the same
    /// AI*; the fingerprint is a drift guard over positions, analytics and the call/combination counters.
    /// </summary>
    private const string ExpectedDefaultScenarioFingerprint =
        "tick=1500|ball=20.993334,-11.877624|players=1:-48.419292,-1.0424956;2:-10.467587,20.892015;" +
        "3:-7.643568,-23.321465;4:-12.888739,6.594845;5:-12.613922,-9.653878;6:9.6677685,-10.243951;" +
        "7:24.9785,19.81547;8:14.669028,-1.5895516;9:20.887981,-12.391231;10:17.48829,-11.871004;" +
        "11:24.981127,-20.435291;12:46.55118,-2.0004814;13:27.826225,18.954184;14:25.03882,-18.430696;" +
        "15:25.899319,3.0480578;16:24.437826,-22.266811;17:38.337223,-7.825693;18:3.2360249,19.31883;" +
        "19:25.253723,-11.181127;20:-2.3199544,-1.8124101;21:35.307102,-10.732671;22:1.5693433,-18.130646;" +
        "|analytics=1147,353,583,94,11,3,10,1,0,0,0,0|calls=193,4302,187,0|" +
        "combinations=4,1,0,0,1,1,0";
}
