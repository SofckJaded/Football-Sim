using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;
using FootballSim;
using FootballSim.Core;
using FootballSim.Input;
using FootballSim.Locomotion;
using FootballSim.Rules;

namespace FootballSim.Tests;

/// <summary>
/// Audit F23 coverage gates that Batch 20 left open: a real recorded-command replay (the Phase 0 test
/// used to run two fresh AI matches and call that a replay), matches that are actually completed
/// through both halves to full time, and a multi-seed tactical report that is an assertion instead of
/// printed console output.
/// </summary>
public sealed class ReplayAndCoverageTests
{
    private readonly ITestOutputHelper _output;

    public ReplayAndCoverageTests(ITestOutputHelper output) => _output = output;

    // =============================================================================================
    // F23 — a real recorded-command replay, per-match isolated
    // =============================================================================================

    private readonly record struct TickState(
        Vector3 BallPosition,
        Vector3 BallVelocity,
        Vector3[] PlayerPositions,
        Vector3[] PlayerVelocities,
        int HomeScore,
        int AwayScore,
        MatchPhase Phase,
        float ClockSeconds);

    private static TickState Capture(MatchSimulationEngine engine) => new(
        engine.Ball.Position,
        engine.Ball.Velocity,
        engine.Players.Select(p => p.Position).ToArray(),
        engine.Players.Select(p => p.Velocity).ToArray(),
        engine.Rules.HomeScore,
        engine.Rules.AwayScore,
        engine.Rules.Phase,
        engine.Rules.MatchClockSeconds);

    private static void AssertSameTick(TickState expected, MatchSimulationEngine replay, int tick)
    {
        Assert.Equal(tick, replay.CurrentTick);
        Assert.Equal(expected.BallPosition, replay.Ball.Position);
        Assert.Equal(expected.BallVelocity, replay.Ball.Velocity);
        Assert.Equal(expected.HomeScore, replay.Rules.HomeScore);
        Assert.Equal(expected.AwayScore, replay.Rules.AwayScore);
        Assert.Equal(expected.Phase, replay.Rules.Phase);
        Assert.Equal(expected.ClockSeconds, replay.Rules.MatchClockSeconds);

        Assert.Equal(expected.PlayerPositions.Length, replay.Players.Count);
        for (int i = 0; i < expected.PlayerPositions.Length; i++)
        {
            FootballerState player = replay.Players[i];
            Assert.Equal(expected.PlayerPositions[i], player.Position);
            Assert.Equal(expected.PlayerVelocities[i], player.Velocity);
        }
    }

    private static MatchSimulationEngine BuildReplay(
        PreMatchConfig config,
        IReadOnlyDictionary<int, Dictionary<int, FootballerCommand>> commandsByPlayer)
    {
        var replay = new MatchSimulationEngine(config);
        foreach (FootballerState player in replay.Players)
        {
            Assert.True(commandsByPlayer.TryGetValue(player.Id, out var commands),
                $"no recording for Player #{player.Id}");
            replay.OverrideController(
                player.Id,
                new RecordedCommandController(
                    ControllerSourceType.Scripted, commands!, () => replay.CurrentTick));
        }

        return replay;
    }

    /// <summary>
    /// Audit F23: the "same seed and recorded commands" test never recorded anything — it stepped two
    /// fresh AI matches and compared them. This records every player's decided command from a live AI
    /// match and replays it into two independent engines stepped interleaved, asserting that every
    /// single tick (ball, players, score, phase, clock) is reproduced bit-for-bit.
    /// </summary>
    [Fact]
    public void F23_RecordedCommandReplay_ReproducesEveryTickOfAMatch()
    {
        const int ticks = 900;
        var config = new PreMatchConfig { Seed = 24680UL, PlayersPerTeam = 5, HalfDurationSeconds = 600f };

        // 1. Record the live match: each player's decided command plus the authoritative state.
        var original = new MatchSimulationEngine(config);
        var commandsByPlayer = original.Players.ToDictionary(
            p => p.Id, _ => new Dictionary<int, FootballerCommand>());
        var recording = new List<TickState>(ticks);

        for (int i = 0; i < ticks; i++)
        {
            original.StepTick();
            int tick = original.CurrentTick;
            foreach (FootballerState player in original.Players)
                commandsByPlayer[player.Id][tick] = player.LastCommand;
            recording.Add(Capture(original));
        }

        // The recording must contain real play, otherwise the replay proves nothing.
        Assert.True(original.Analytics.HomePassesAttempted + original.Analytics.AwayPassesAttempted > 0,
            "the recorded match never passed the ball");

        // 2. Two independent replays, stepped interleaved so shared/static state would show up.
        var replayA = BuildReplay(config, commandsByPlayer);
        var replayB = BuildReplay(config, commandsByPlayer);

        for (int i = 0; i < recording.Count; i++)
        {
            replayA.StepTick();
            replayB.StepTick();
            AssertSameTick(recording[i], replayA, i + 1);
            AssertSameTick(recording[i], replayB, i + 1);
        }

        // 3. The outcome and the execution metrics match too, not only the positions.
        Assert.Equal(original.Rules.HomeScore, replayA.Rules.HomeScore);
        Assert.Equal(original.Rules.AwayScore, replayA.Rules.AwayScore);
        Assert.Equal(original.Analytics.HomePassesAttempted, replayA.Analytics.HomePassesAttempted);
        Assert.Equal(original.Analytics.AwayPassesAttempted, replayA.Analytics.AwayPassesAttempted);
        Assert.Equal(original.Analytics.HomePassesCompleted, replayA.Analytics.HomePassesCompleted);
        Assert.Equal(original.Analytics.HomeShots, replayA.Analytics.HomeShots);
        Assert.Equal(original.Analytics.HomePossessionTicks, replayA.Analytics.HomePossessionTicks);
        Assert.Equal(original.Rules.Events.Count, replayA.Rules.Events.Count);

        _output.WriteLine(
            $"replayed {ticks} ticks x2 engines: score {original.Rules.HomeScore}-{original.Rules.AwayScore}, " +
            $"passes {original.Analytics.HomePassesAttempted}/{original.Analytics.AwayPassesAttempted}, " +
            $"shots {original.Analytics.HomeShots}/{original.Analytics.AwayShots}");
    }

    // =============================================================================================
    // F23 — both halves and full time are actually reached and asserted
    // =============================================================================================

    /// <summary>
    /// Audit F23: the tactical test ran 90 seconds and never through half time; the CLI printed
    /// "Match Complete" when it hit a tick cap. A match must reach half time, switch halves, reach
    /// full time, and stop there.
    /// </summary>
    [Fact]
    public void F23_ShortMatch_ReachesHalfTimeAndFullTimeWithBothHalvesPlayed()
    {
        const float halfSeconds = 12f;
        var sim = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = 808UL,
            PlayersPerTeam = 5,
            HalfDurationSeconds = halfSeconds
        });

        bool sawHalfTime = false;
        bool sawSecondHalf = false;
        float secondHalfDirection = 0f;
        var phasesSeen = new List<MatchPhase>();

        int guard = 0;
        while (sim.Rules.Phase != MatchPhase.FullTime && guard++ < 20000)
        {
            sim.StepTick();
            if (phasesSeen.Count == 0 || phasesSeen[^1] != sim.Rules.Phase)
                phasesSeen.Add(sim.Rules.Phase);

            if (sim.Rules.Phase == MatchPhase.HalfTime) sawHalfTime = true;
            if (sim.Rules.Period == MatchPeriod.SecondHalf)
            {
                sawSecondHalf = true;
                secondHalfDirection = sim.Rules.GetHomeAttackDirectionX();
            }
        }

        Assert.True(sawHalfTime, "the match never reached half time");
        Assert.True(sawSecondHalf, "the match never switched to the second half");
        Assert.Equal(MatchPhase.FullTime, sim.Rules.Phase);

        // The clock covers both halves; the half-time pause is not match time.
        Assert.InRange(sim.Rules.MatchClockSeconds, halfSeconds * 2f, halfSeconds * 2f + 0.5f);

        // The teams change ends at half time.
        Assert.Equal(-1f, secondHalfDirection);
        Assert.Equal(-1f, sim.Rules.GetHomeAttackDirectionX());

        // Exactly one half-time and one full-time event, in that order.
        var milestone = sim.Rules.Events.Where(e => e.EventType is "HalfTime" or "FullTime").ToList();
        Assert.Equal(2, milestone.Count);
        Assert.Equal("HalfTime", milestone[0].EventType);
        Assert.Equal("FullTime", milestone[1].EventType);
        Assert.Contains(nameof(MatchPhase.HalfTime), phasesSeen.Select(p => p.ToString()));

        // Full time is terminal: stepping again changes nothing (no extra clock, ticks or events).
        int ticksAtFullTime = sim.CurrentTick;
        int eventsAtFullTime = sim.Rules.Events.Count;
        float clockAtFullTime = sim.Rules.MatchClockSeconds;
        for (int i = 0; i < 30; i++) sim.StepTick();

        Assert.Equal(ticksAtFullTime, sim.CurrentTick);
        Assert.Equal(eventsAtFullTime, sim.Rules.Events.Count);
        Assert.Equal(clockAtFullTime, sim.Rules.MatchClockSeconds);
        Assert.Equal(MatchPhase.FullTime, sim.Rules.Phase);

        _output.WriteLine(
            $"half {halfSeconds}s: phases {string.Join(" -> ", phasesSeen)}, " +
            $"full time at tick {ticksAtFullTime} clock {clockAtFullTime:F2}s, " +
            $"score {sim.Rules.HomeScore}-{sim.Rules.AwayScore}");
    }

    // =============================================================================================
    // F23 — a multi-seed tactical gate with real execution metrics
    // =============================================================================================

    /// <summary>
    /// Audit F23: tactical differentiation was asserted for one seed only (and named for a pass ratio
    /// it never checked), and the multi-seed CLI report was not a gate.
    ///
    /// This runs four seeds through a complete four-minute match each and asserts two different things:
    /// <list type="bullet">
    /// <item>every seed plays a real, healthy, completed match with execution-consistent metrics;</item>
    /// <item>the profile separation is measured across the set. Individual match shapes are chaotic
    /// (a two-minute window can invert on one seed — see `PROGRESS.md` warning 3), so the gate is the
    /// average width difference plus a majority of individual seeds, not a claim about every seed.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void F23_MultiSeedTacticalReport_HoldsAcrossTheSeedSet()
    {
        ulong[] seeds = { 2026UL, 4242UL, 91UL, 777UL };
        var failures = new List<string>();
        var widthDifferences = new List<float>();

        foreach (ulong seed in seeds)
        {
            var sim = new MatchSimulationEngine(new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = 120f,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack
            });

            int requestedKicks = 0;
            int executedKicks = 0;
            int lastAttemptTick = -1;
            int guard = 0;

            while (sim.Rules.Phase != MatchPhase.FullTime && guard++ < 30000)
            {
                sim.StepTick();

                // Execution-aware request count, exactly like the probe: one row per contact attempt.
                IReadOnlyList<BallContactAttempt> attempts = sim.LastContactAttempts;
                if (attempts.Count > 0 && attempts[0].Tick != lastAttemptTick)
                {
                    lastAttemptTick = attempts[0].Tick;
                    foreach (BallContactAttempt attempt in attempts)
                    {
                        if (!attempt.WantedKick) continue;
                        requestedKicks++;
                        if (attempt.Executed) executedKicks++;
                    }
                }
            }

            MatchAnalyticsSummary a = sim.Analytics;
            bool completed = sim.Rules.Phase == MatchPhase.FullTime;
            // A4: the profile comparison is made on the possession-scoped span, the shape a team holds
            // with the ball. The unconditional span is reported beside it.
            float widthDifference = a.HomeAttackingWidthMeters - a.AwayAttackingWidthMeters;
            widthDifferences.Add(widthDifference);

            _output.WriteLine(
                $"Seed {seed}: FT={completed} clock={sim.Rules.MatchClockSeconds:F1}s " +
                $"score={a.HomeGoals}-{a.AwayGoals} width={a.HomeAttackingWidthMeters:F1}/{a.AwayAttackingWidthMeters:F1}m " +
                $"(all-tick {a.HomeAverageTeamWidthMeters:F1}/{a.AwayAverageTeamWidthMeters:F1}m) " +
                $"passes={a.HomePassesAttempted}/{a.AwayPassesAttempted} completed={a.HomePassesCompleted}/{a.AwayPassesCompleted} " +
                $"shots={a.HomeShots}/{a.AwayShots} possession={a.HomePossessionShare * 100f:F1}% " +
                $"controlled={a.HomeControlledPossessionShare * 100f:F1}% kicks={executedKicks}/{requestedKicks}");

            if (!completed) failures.Add($"seed {seed}: did not reach full time");
            if (a.HomePassesAttempted == 0 || a.AwayPassesAttempted == 0)
                failures.Add($"seed {seed}: only {a.HomePassesAttempted}/{a.AwayPassesAttempted} passes attempted");
            if (a.HomeShots + a.AwayShots == 0)
                failures.Add($"seed {seed}: no shots");
            if (requestedKicks == 0)
                failures.Add($"seed {seed}: no kick was ever requested");
            if (executedKicks > requestedKicks)
                failures.Add($"seed {seed}: {executedKicks} kicks executed out of {requestedKicks} requested");
            if (a.HomePassesCompleted > a.HomePassesAttempted || a.AwayPassesCompleted > a.AwayPassesAttempted)
                failures.Add($"seed {seed}: more pass completions than attempts");

            // Metrics that must be finite and inside their declared range for every seed.
            Assert.True(float.IsFinite(a.HomeAverageTeamWidthMeters) && a.HomeAverageTeamWidthMeters >= 0f);
            Assert.True(float.IsFinite(a.HomeAttackingWidthMeters) && a.HomeAttackingWidthMeters >= 0f);
            Assert.True(float.IsFinite(a.HomePossessionShare));
            Assert.InRange(a.HomePossessionShare, 0f, 1f);
        }

        // Profile separation across the seed set: the possession side is wider on the average and on
        // the majority of individual matches (one chaotic match may not express it).
        float meanDifference = widthDifferences.Average();
        int widerSeeds = widthDifferences.Count(d => d > 0f);
        _output.WriteLine(
            $"width separation: mean {meanDifference:F2} m, seeds wider {widerSeeds}/{seeds.Length} " +
            $"[{string.Join(", ", widthDifferences.Select(d => $"{d:F2}"))}]");

        Assert.True(widerSeeds >= seeds.Length - 1,
            $"the possession profile was wider on only {widerSeeds}/{seeds.Length} seeds");
        Assert.True(meanDifference > 0.5f,
            $"mean width separation was only {meanDifference:F2} m across {seeds.Length} seeds");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
