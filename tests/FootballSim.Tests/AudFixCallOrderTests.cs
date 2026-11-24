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
    ///
    /// <para><b>AIB-07..AIB-12 re-capture (the whole 2026-11-23 ai57-review batch):</b> six behaviour groups
    /// move the shipped path in this match, so the constant is re-captured once at <c>ai58-rs1</c> rather than
    /// exempted. (1) Defensive duties no longer take the attacking space/lane reward or a stepped-out variant;
    /// they score goal-side position and lane screening, are clamped at the live line band's ceiling, and the
    /// vacated-gap depth is clamped to the same band. (2) The lane test sees a moving defender at his real
    /// intercept reach plus bounded doubt, with lookahead step-in points and a reduced reaction once he is
    /// already closing in, and the corridor metric prices his projected position. (3) A space promise is refused
    /// when the planner's calibrated roll leaves the promised region before the receiver arrives, and
    /// promised/timed receptions price defenders against <c>max(flight, receiverArrival)</c>. (4) Every
    /// advertised <c>ToRun</c> ball carries the crisp power floor. (5) The finish aims away from the perceived
    /// keeper instead of always at the -Z corner, an open carry gains the shot-value improvement of the ground
    /// ahead, the early-carry bonus is 0.10 over 0.35 s instead of 0.25 over 0.70 s, and the carrier's sprint
    /// follows the path ahead rather than the nearest body. The AUD-13c property this constant guards is still
    /// pinned by the zero-delay tests above; this remains a bit-for-bit tripwire for the shipped path. The
    /// fix report is <c>docs/Bug_Fixes_2026-11-23_AI57Review.md</c>.</para>
    ///
    /// <para><b>2026-11-24 pass-interception re-capture (<c>ai60-rs1</c>):</b> the proposal-layer lane and
    /// completion fixes of <c>docs/task-pass-interception-fixes.md</c> move the shipped path in this match —
    /// a defender sprinting back into the corridor from past the receiver is now sampled, the step-in test
    /// asks down-lane points, the passer's turn lead-in is charged to a close presser, and a clean lane no
    /// longer rescues a decisively lost arrival race — so the constant is re-captured once at
    /// <c>ai60-rs1</c>. The AUD-13c property is still pinned by the zero-delay tests above; this remains a
    /// bit-for-bit drift tripwire, not a claim that AI changes never move a match. The fix report is
    /// <c>docs/Bug_Fixes_2026-11-24_PassInterception.md</c>.</para>
    ///
    /// <para><b>2026-11-24 reach-model re-capture (<c>ai62-rs1</c>):</b> the
    /// <see cref="AI.TimeToReachModel"/> fix of <c>docs/task-reach-and-carrier-perception-fixes.md</c> — a
    /// body with sideways momentum is now reachable and steers onto the target instead of being declared
    /// unreachable — moves every lane, arrival race, space race and ball-responder election in this match, so
    /// the constant is re-captured once at <c>ai62-rs1</c>. This re-capture is a documented drift step, not a
    /// new equivalence claim: the 1500-tick window contains 886/598 possession ticks, 464/189 controlled
    /// possession ticks and 9/7 attempted, 7/5 completed passes on this tree. The AUD-13c property is still
    /// pinned by the zero-delay tests above. The fix report is
    /// <c>docs/Bug_Fixes_2026-11-24_ReachAndPerception.md</c>.</para>
    ///
    /// <para><b>2026-11-24 playtest &amp; ai62-review re-capture (<c>ai63-rs1</c>):</b> the Law 11
    /// through-ball target fix (<c>ToSpace</c> and 1-2 return regions), receiver sprint promotion +
    /// distance-scaled short-pass roll-out bound, past-target up-lane <c>IsLaneBlocked</c> lookahead,
    /// boxed-in midfield escape clearance, and <see cref="AI.TimeToReachModel"/> lead-intercept / steering
    /// fixes move this match (1062/408 possession ticks, 678/157 controlled ticks, 7/4 attempted, 6/2
    /// completed passes, 1/0 shots/goals), so the constant is re-captured at <c>ai63-rs1</c>. The AUD-13c
    /// property is still pinned by the zero-delay tests above.</para>
    /// </summary>
    private const string ExpectedDefaultScenarioFingerprint =
        "tick=1500|ball=1.1767579,7.466493|players=1:-48.33269,0.5247074;2:-17.190868,17.617447;" +
        "3:-18.896147,-22.051704;4:-17.245481,7.7456274;5:-17.716873,-7.917333;6:-14.760573,2.8962784;" +
        "7:-1.8418686,11.772882;8:-5.7770395,7.3850117;9:3.1432638,0.4255827;10:-7.442069,-5.9277806;" +
        "11:0.061654218,-15.803591;12:39.397926,2.4202883;13:19.56288,21.061003;14:23.039347,-21.385536;" +
        "15:19.545475,7.3450727;16:22.195406,-7.155316;17:16.929895,0.49664968;18:1.0403203,17.420986;" +
        "19:2.3889,8.796581;20:-1.828904,2.2146654;21:13.342132,-4.455827;22:5.661997,-19.262606;" +
        "|analytics=1062,408,678,119,7,5,6,3,1,0,1,0|calls=197,4894,181,0|" +
        "combinations=6,1,0,0,1,0,2";
}
