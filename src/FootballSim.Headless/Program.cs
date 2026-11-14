using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FootballSim;
using FootballSim.AI;
using FootballSim.Core;
using FootballSim.Locomotion;
using FootballSim.Physics;
using FootballSim.Rules;
using FootballSim.Scenarios;

namespace FootballSim.Headless;

public static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "--help";

        switch (mode)
        {
            case "--physics-lab":
                RunPhysicsLab();
                return 0;

            case "--match-5v5":
                return RunHeadlessMatch(playersPerTeam: 5, halfDurationSeconds: 300f, seed: 101UL) ? 0 : 1;

            case "--match-11v11":
                return RunHeadlessMatch(playersPerTeam: 11, halfDurationSeconds: 2700f, seed: 20260818UL) ? 0 : 1;

            case "--tactical-batch":
                return RunTacticalComparisonBatch() ? 0 : 1;

            case "--scenario":
                return RunScenarioCommand(args);

            case "--perf":
                return RunPerformanceSample(args) ? 0 : 1;

            case "--deliveries":
                return RunDeliveryEvidence(args) ? 0 : 1;

            default:
                Console.WriteLine("FootballSim.Headless CLI Runner");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --physics-lab");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --match-5v5");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --match-11v11");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --tactical-batch");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --scenario list");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --scenario build-up-3v2 [ticks]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --perf [ticks]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --deliveries [seeds] [halfSeconds]");
                Console.WriteLine();
                Console.WriteLine("Every match mode exits non-zero when the match did not actually reach full time");
                Console.WriteLine("(audit F23: hitting the tick cap is no longer reported as a completed match).");
                return 0;
        }
    }

    /// <summary>
    /// Runs one advanced-AI scenario fixture. This exists so a behaviour can be reproduced from the
    /// command line without writing a test — the same <see cref="ScenarioLibrary"/> the suite uses.
    /// </summary>
    private static int RunScenarioCommand(string[] args)
    {
        if (args.Length < 2 || args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Available scenarios:");
            foreach (ScenarioLibrary.Definition listed in ScenarioLibrary.All)
                Console.WriteLine($"  {listed.Key,-26} {listed.Intent}");
            return 0;
        }

        string key = args[1];
        ScenarioLibrary.Definition? definition =
            ScenarioLibrary.All.FirstOrDefault(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            Console.WriteLine($"Unknown scenario '{key}'. Run --scenario list.");
            return 1;
        }

        int ticks = args.Length > 2 && int.TryParse(args[2], out int requestedTicks) && requestedTicks > 0
            ? requestedTicks
            : 600;

        ScenarioRunResult result = ScenarioRunner.Run(definition.Build(), ticks, traceDecisions: true);

        Console.WriteLine($"=== Scenario '{definition.Key}' — {ticks} tick budget ===");
        Console.WriteLine($"Intent            : {definition.Intent}");
        Console.WriteLine(result.Describe());
        Console.WriteLine($"Possession        : {result.Possession.State} | team {result.Possession.PossessionTeam} " +
                          $"| carrier {result.Possession.ControllingPlayerId} | receiver {result.Possession.IntendedReceiverId}");
        Console.WriteLine($"Contacts          : {result.Contacts.Count} executed, {result.ContactAttempts.Count} requested");
        foreach (MatchRuleEvent ruleEvent in result.RuleEvents)
            Console.WriteLine($"  rule @{ruleEvent.MatchTimeSeconds:F1}s {ruleEvent.EventType}: {ruleEvent.Details}");

        Console.WriteLine("Decisions (last 8):");
        int shown = 0;
        for (int i = result.Decisions.Count - 1; i >= 0 && shown < 8; i--, shown++)
            Console.WriteLine($"  {result.Decisions[i].Describe()}");

        return result.ReachedFullTime || result.TicksRun == ticks ? 0 : 1;
    }

    /// <summary>
    /// Same-machine performance sample for the advanced-AI work (A0.5 / A9.3).
    ///
    /// Per-tick wall time is reported as percentiles because an average hides the spike that actually
    /// breaks the 16.67 ms fixed step. Allocation is reported per tick from the thread's allocation
    /// counter, so a regression that allocates per player shows up immediately.
    /// </summary>
    private static bool RunPerformanceSample(string[] args)
    {
        int measureTicks = args.Length > 1 && int.TryParse(args[1], out int requested) && requested > 0
            ? requested
            : 20_000;
        const int warmupTicks = 2_000;

        var config = new PreMatchConfig
        {
            Seed = 20260818UL,
            PlayersPerTeam = 11,
            HalfDurationSeconds = 3600f,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack
        };

        var engine = new MatchSimulationEngine(config);
        for (int i = 0; i < warmupTicks; i++) engine.StepTick();

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        var samples = new double[measureTicks];
        var stopwatch = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < measureTicks; i++)
        {
            stopwatch.Restart();
            engine.StepTick();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        int gen0After = GC.CollectionCount(0);
        int gen1After = GC.CollectionCount(1);
        int gen2After = GC.CollectionCount(2);

        Array.Sort(samples);
        static double Percentile(double[] sorted, double fraction) =>
            sorted[Math.Clamp((int)(fraction * (sorted.Length - 1)), 0, sorted.Length - 1)];

        double mean = 0d;
        for (int i = 0; i < samples.Length; i++) mean += samples[i];
        mean /= samples.Length;

        double total = 0d;
        for (int i = 0; i < samples.Length; i++) total += samples[i];

        double allocatedPerTick = (double)(allocatedAfter - allocatedBefore) / measureTicks;

        Console.WriteLine("=== Advanced-AI performance sample (11v11, 22 active players) ===");
        Console.WriteLine($"Simulation version     : {SimulationVersion.Tag}");
        Console.WriteLine($"Runtime                : {Environment.Version} | {Environment.OSVersion}");
        Console.WriteLine($"CPU                    : {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}");
        Console.WriteLine($"Ticks                  : {warmupTicks} warm-up + {measureTicks} measured");
        Console.WriteLine($"Per-tick mean          : {mean * 1000d:F1} us");
        Console.WriteLine($"Per-tick p50 / p95     : {Percentile(samples, 0.50) * 1000d:F1} / {Percentile(samples, 0.95) * 1000d:F1} us");
        Console.WriteLine($"Per-tick p99 / max     : {Percentile(samples, 0.99) * 1000d:F1} / {samples[^1] * 1000d:F1} us");
        Console.WriteLine($"Fixed-step budget      : 16666.7 us (60 Hz)");
        Console.WriteLine($"Total simulated        : {total:F1} ms for {measureTicks} ticks");
        Console.WriteLine($"Allocated per tick     : {allocatedPerTick:F1} bytes");
        Console.WriteLine($"GC collections (0/1/2) : {gen0After - gen0Before}/{gen1After - gen1Before}/{gen2After - gen2Before}");

        bool withinBudget = Percentile(samples, 0.99) * 1000d <= 16666.7d;
        Console.WriteLine(withinBudget ? "BUDGET: 60 Hz step holds at p99" : "BUDGET: p99 exceeds the 60 Hz step");
        return withinBudget;
    }

    private static void RunPhysicsLab()
    {
        Directory.CreateDirectory("artifacts");
        // Free-kick style curled shot: 26 m/s launch speed, 65 rad/s sidespin
        string csv = BallPhysicsSolver.RunTrajectoryLabCsv(
            initialPosition: new Vector3(0f, 0.11f, 0f),
            initialVelocity: new Vector3(24.0f, 6.8f, 0f),
            initialSpinRadPerSec: new Vector3(0f, 65.0f, -20.0f),
            durationSeconds: 3.5f);

        string outPath = Path.Combine("artifacts", "ball_curled_freekick_trace.csv");
        File.WriteAllText(outPath, csv);
        Console.WriteLine($"[Phase 1 Physics Lab] Wrote trajectory trace to {outPath}");
    }

    /// <summary>
    /// A5.5b evidence: <b>offered versus chosen</b> for every delivery the carrier can consider, over a seed set.
    ///
    /// A5.5 knew how many deliveries were *executed* (the match report prints that), and that number is why
    /// the milestone was not ticked: zero crosses in a full match cannot distinguish "the cross is never
    /// offered" from "the cross is offered and always loses". This probe exists to tell those two apart. It
    /// reads the carrier's own candidate table after each decision — no simulation state, no extra behaviour —
    /// and reports, per delivery family:
    ///
    /// <list type="bullet">
    /// <item><b>offered</b> — live decision ticks where the family appeared in the carrier's action set;</item>
    /// <item><b>chosen</b> — how many separate selections the family won (a new choice, not a held one);</item>
    /// <item><b>executed</b> — balls the execution layer actually struck, from its own contacts;</item>
    /// <item><b>margin</b> — how far the family's best score was from the best non-delivery action on the
    ///   same tick (positive = the delivery lost by that much, negative = it was in front);</item>
    /// <item><b>p / gain / loss</b> — the winning delivery's own value terms, so a lost offer says which of
    ///   the three levers is off instead of being a bare number.</item>
    /// </list>
    ///
    /// The counts are ticks, not events: a carrier who holds the same action for forty ticks counts forty
    /// times. That is the right denominator for "was it on the table", and the chosen/executed columns are
    /// what carry the decision evidence.
    /// </summary>
    private static bool RunDeliveryEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 900f;

        // The families the plan asks to be compared (A5.5). Carry and recycle are the baseline the
        // deliveries have to beat; the four delivery types are the ones A5.5b must make win when they should.
        string[] familyNames = { "cross", "lofted", "driven", "cutback", "ground-run", "carry", "recycle" };
        const int FamilyCount = 7;

        Console.WriteLine($"=== A5.5b delivery evidence: offered vs chosen, {seedCount} seeds x {halfSeconds * 2f:F0} s ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Units              : offered/chosen are live carrier decision ticks (a held action counts every");
        Console.WriteLine("                     tick it is held); executed is from the execution layer's own contacts;");
        Console.WriteLine("                     margin = best non-delivery score - the delivery's best score (>0 = it lost).");

        var offered = new int[FamilyCount];
        var chosen = new int[FamilyCount];
        var executed = new int[FamilyCount];
        var marginSum = new float[FamilyCount];
        var marginWorst = new float[FamilyCount];
        var marginTicks = new int[FamilyCount];
        var probabilitySum = new float[FamilyCount];
        var gainSum = new float[FamilyCount];
        var lossSum = new float[FamilyCount];
        var termsTicks = new int[FamilyCount];
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);
        int matchesPlayed = 0;
        int decoyTicksTotal = 0;
        int decoyAdoptionsTotal = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 7100UL + (ulong)(seedIndex + 1);
            var engine = new MatchSimulationEngine(new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack
            });

            var seedOffered = new int[FamilyCount];
            var seedChosen = new int[FamilyCount];
            var seedExecuted = new int[FamilyCount];
            var seedMarginSum = new float[FamilyCount];
            var seedMarginWorst = new float[FamilyCount];
            var seedMarginTicks = new int[FamilyCount];
            var seedProbability = new float[FamilyCount];
            var seedGain = new float[FamilyCount];
            var seedLoss = new float[FamilyCount];
            var seedTermsTicks = new int[FamilyCount];

            IReadOnlyList<FootballerState> players = engine.Players;
            var previousFamily = new int[players.Count];
            Array.Fill(previousFamily, -2);   // -2 = nothing selected yet, so the first choice counts
            int decoyTicks = 0;
            int decoyAdoptions = 0;
            var previousDecoy = new bool[players.Count];

            var familyBest = new float[FamilyCount];
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            int lastAttemptTick = -1;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();

                for (int index = 0; index < players.Count; index++)
                {
                    FootballerState player = players[index];
                    if (!player.IsActiveOnPitch) continue;
                    if (engine.GetController(player.Id) is not TacticalUtilityAiController ai) continue;

                    Array.Fill(familyBest, float.NegativeInfinity);
                    float bestOther = float.NegativeInfinity;
                    IReadOnlyList<UtilityCandidate> candidates = ai.LastCandidates;

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        UtilityCandidate candidate = candidates[c];
                        int family = FamilyOf(candidate.Key);
                        if (family < 0)
                        {
                            if (candidate.UtilityScore > bestOther) bestOther = candidate.UtilityScore;
                            continue;
                        }

                        if (candidate.UtilityScore <= familyBest[family]) continue;
                        familyBest[family] = candidate.UtilityScore;
                        if (!candidate.Terms.IsPresent) continue;
                        seedProbability[family] += candidate.Terms.Probability01;
                        seedGain[family] += candidate.Terms.Gain;
                        seedLoss[family] += candidate.Terms.Loss;
                        seedTermsTicks[family]++;
                    }

                    for (int family = 0; family < FamilyCount; family++)
                    {
                        if (float.IsNegativeInfinity(familyBest[family])) continue;

                        seedOffered[family]++;
                        if (float.IsNegativeInfinity(bestOther)) continue;

                        float margin = bestOther - familyBest[family];
                        seedMarginSum[family] += margin;
                        seedMarginTicks[family]++;
                        if (margin > seedMarginWorst[family]) seedMarginWorst[family] = margin;
                    }

                    int selectedFamily = FamilyOf(ai.LastSelected.Key);
                    if (selectedFamily != previousFamily[index])
                    {
                        previousFamily[index] = selectedFamily;
                        if (selectedFamily >= 0) seedChosen[selectedFamily]++;
                    }

                    // A5.6: the decoy is a run, not a delivery, so it is counted separately: how many ticks
                    // an AI player is holding a decoy intent, and how many separate adoptions that took.
                    bool decoyHeld = ai.Runs.Intent?.Kind == RunKind.Decoy;
                    if (decoyHeld)
                    {
                        decoyTicks++;
                        if (!previousDecoy[index]) decoyAdoptions++;
                    }

                    previousDecoy[index] = decoyHeld;
                }

                IReadOnlyList<BallContactAttempt> attempts = engine.LastContactAttempts;
                if (attempts.Count > 0 && attempts[0].Tick != lastAttemptTick)
                {
                    lastAttemptTick = attempts[0].Tick;
                    for (int a = 0; a < attempts.Count; a++)
                    {
                        if (!attempts[a].WantedKick || !attempts[a].Executed) continue;
                        int family = FamilyOfIntent(attempts[a].Intent);
                        if (family >= 0) seedExecuted[family]++;
                    }
                }
            }

            // Why a delivery was refused (A4.6's rejection list), counted for the whole match. This is the
            // other half of "never chosen": a cross refused for low completion never reached the table.
            for (int index = 0; index < players.Count; index++)
            {
                if (engine.GetController(players[index].Id) is not TacticalUtilityAiController ai) continue;
                for (int r = 0; r < ai.RejectionCount; r++)
                {
                    string reason = ai.GetRejection(r).Reason;
                    rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matchesPlayed++;
            decoyTicksTotal += decoyTicks;
            decoyAdoptionsTotal += decoyAdoptions;
            var analytics = engine.Analytics;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks | " +
                              $"score {analytics.HomeGoals}-{analytics.AwayGoals} | " +
                              $"passes {analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} " +
                              $"({analytics.HomePassCompletionShare * 100f:F0}%/{analytics.AwayPassCompletionShare * 100f:F0}%) | " +
                              $"shots {analytics.HomeShots}/{analytics.AwayShots}");

            for (int family = 0; family < FamilyCount; family++)
            {
                Accumulate(offered, chosen, executed, marginSum, marginWorst, marginTicks,
                    probabilitySum, gainSum, lossSum, termsTicks,
                    family, seedOffered, seedChosen, seedExecuted, seedMarginSum, seedMarginWorst,
                    seedMarginTicks, seedProbability, seedGain, seedLoss, seedTermsTicks);

                string margin = seedMarginTicks[family] > 0
                    ? $"{seedMarginSum[family] / seedMarginTicks[family]:F2} (worst {seedMarginWorst[family]:F2})"
                    : "n/a";
                string terms = seedTermsTicks[family] > 0
                    ? $"p {seedProbability[family] / seedTermsTicks[family]:F2} " +
                      $"gain {seedGain[family] / seedTermsTicks[family]:F2} " +
                      $"loss {seedLoss[family] / seedTermsTicks[family]:F2}"
                    : "no terms";
                Console.WriteLine($"  {familyNames[family],-11}: offered {seedOffered[family],6} | chosen {seedChosen[family],4} | " +
                                  $"executed {seedExecuted[family],4} | margin {margin} | {terms}");
            }

            Console.WriteLine($"  decoy runs : held {decoyTicks,8} ticks | adopted {decoyAdoptions,5} times");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matchesPlayed} completed) ===");
        for (int family = 0; family < FamilyCount; family++)
        {
            string margin = marginTicks[family] > 0
                ? $"{marginSum[family] / marginTicks[family]:F2} (worst {marginWorst[family]:F2})"
                : "n/a";
            string terms = termsTicks[family] > 0
                ? $"p {probabilitySum[family] / termsTicks[family]:F2} " +
                  $"gain {gainSum[family] / termsTicks[family]:F2} " +
                  $"loss {lossSum[family] / termsTicks[family]:F2}"
                : "no terms";
            Console.WriteLine($"  {familyNames[family],-11}: offered {offered[family],7} | chosen {chosen[family],5} | " +
                              $"executed {executed[family],5} | margin {margin} | {terms}");
        }

        Console.WriteLine("Rejections        : " + (rejections.Count == 0
            ? "none"
            : string.Join(", ", rejections.Select(pair => $"[{pair.Key}, {pair.Value}]"))));
        Console.WriteLine($"Decoy runs (A5.6) : held {decoyTicksTotal} ticks | adopted {decoyAdoptionsTotal} times");

        return matchesPlayed == seedCount;
    }

    private static void Accumulate(
        int[] offered, int[] chosen, int[] executed, float[] marginSum, float[] marginWorst, int[] marginTicks,
        float[] probabilitySum, float[] gainSum, float[] lossSum, int[] termsTicks,
        int family,
        int[] seedOffered, int[] seedChosen, int[] seedExecuted, float[] seedMarginSum, float[] seedMarginWorst,
        int[] seedMarginTicks, float[] seedProbability, float[] seedGain, float[] seedLoss, int[] seedTermsTicks)
    {
        offered[family] += seedOffered[family];
        chosen[family] += seedChosen[family];
        executed[family] += seedExecuted[family];
        marginSum[family] += seedMarginSum[family];
        marginTicks[family] += seedMarginTicks[family];
        if (seedMarginWorst[family] > marginWorst[family]) marginWorst[family] = seedMarginWorst[family];
        probabilitySum[family] += seedProbability[family];
        gainSum[family] += seedGain[family];
        lossSum[family] += seedLoss[family];
        termsTicks[family] += seedTermsTicks[family];
    }

    /// <summary>
    /// Which delivery family a carrier candidate belongs to, or -1 when it is not a delivery. The tags are
    /// the ones the generator puts in the candidate key, so this cannot drift from what was offered.
    /// </summary>
    private static int FamilyOf(in CandidateKey key)
    {
        if (key.Kind == CandidateKind.Carry) return 5;
        if (key.Kind != CandidateKind.Pass) return -1;

        if (string.Equals(key.TargetTag, "Cross", StringComparison.Ordinal)) return 0;
        if (string.Equals(key.TargetTag, "Lofted", StringComparison.Ordinal)) return 1;
        if (string.Equals(key.TargetTag, "Driven", StringComparison.Ordinal)) return 2;
        if (string.Equals(key.TargetTag, "Cutback", StringComparison.Ordinal)) return 3;
        if (string.Equals(key.TargetTag, CandidateGenerator.RunPassTag, StringComparison.Ordinal)) return 4;
        if (string.Equals(key.TargetTag, "Recycle", StringComparison.Ordinal)) return 6;
        return -1;
    }

    /// <summary>The family of an executed kick's own intent (the delivery half of the table).</summary>
    private static int FamilyOfIntent(KickIntentType intent) => intent switch
    {
        KickIntentType.Cross => 0,
        KickIntentType.LoftedPass => 1,
        KickIntentType.LobbedThroughPass => 1,
        KickIntentType.DrivenPass => 2,
        _ => -1
    };

    /// <summary>
    /// Runs one headless match and reports whether it genuinely completed.
    ///
    /// Audit F23: this used to print "Match Complete" whenever the tick cap was reached, so a match
    /// that stalled short of full time looked identical to a finished one. The phase is now asserted
    /// and the process exit code reflects it.
    /// </summary>
    private static bool RunHeadlessMatch(int playersPerTeam, float halfDurationSeconds, ulong seed)
    {
        var config = new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = playersPerTeam,
            HalfDurationSeconds = halfDurationSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack
        };

        var engine = new MatchSimulationEngine(config);
        // Generous cap: restarts and goals pause the clock while ticks keep running.
        int maxTicks = (int)(halfDurationSeconds * 2.25f * 60f) + 1200;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int requestedKicks = 0;
        int executedKicks = 0;
        int lastAttemptTick = -1;
        var refusalReasons = new Dictionary<string, int>();

        // A5.5 evidence: how often the airline deliveries are actually chosen and struck. Counted from the
        // execution layer's contacts (a lost arbitration is not a cross), so a jump here cannot be a
        // decision that never reached the ball.
        int crosses = 0;
        int lofted = 0;
        int driven = 0;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();

            IReadOnlyList<BallContactAttempt> attempts = engine.LastContactAttempts;
            if (attempts.Count == 0 || attempts[0].Tick == lastAttemptTick) continue;
            lastAttemptTick = attempts[0].Tick;
            foreach (BallContactAttempt attempt in attempts)
            {
                if (!attempt.WantedKick) continue;
                requestedKicks++;
                if (attempt.Executed)
                {
                    executedKicks++;
                    switch (attempt.Intent)
                    {
                        case KickIntentType.Cross: crosses++; break;
                        case KickIntentType.LoftedPass:
                        case KickIntentType.LobbedThroughPass: lofted++; break;
                        case KickIntentType.DrivenPass: driven++; break;
                    }
                }
                else refusalReasons[attempt.Reason.ToString()] = refusalReasons.GetValueOrDefault(attempt.Reason.ToString()) + 1;
            }
        }
        sw.Stop();

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        var a = engine.Analytics;

        Console.WriteLine($"=== {playersPerTeam}v{playersPerTeam} Headless Match in {sw.ElapsedMilliseconds} ms ===");
        Console.WriteLine($"Simulation version: {SimulationVersion.Tag} (seed {seed})");
        Console.WriteLine($"Status            : {(completed ? "FULL TIME" : $"INCOMPLETE — tick cap {maxTicks} reached")}" +
                          $" | phase {engine.Rules.Phase} | period {engine.Rules.Period}");
        Console.WriteLine($"Tick / Clock      : tick {engine.CurrentTick} | clock {engine.Rules.MatchClockSeconds:F1}s of {halfDurationSeconds * 2f:F0}s");
        Console.WriteLine($"Final Score       : Home ({config.HomeProfile.Name}) {a.HomeGoals} - {a.AwayGoals} Away ({config.AwayProfile.Name})");
        Console.WriteLine($"Possession Share  : Home {a.HomePossessionShare * 100f:F1}% | Away {(1f - a.HomePossessionShare) * 100f:F1}%");
        Console.WriteLine($"Controlled Ball   : Home {a.HomeControlledPossessionShare * 100f:F1}% | Away {(1f - a.HomeControlledPossessionShare) * 100f:F1}%");
        Console.WriteLine($"Passes Attempted  : Home {a.HomePassesAttempted} | Away {a.AwayPassesAttempted}");
        Console.WriteLine($"Passes Completed  : Home {a.HomePassesCompleted} ({a.HomePassCompletionShare * 100f:F0}%) | Away {a.AwayPassesCompleted} ({a.AwayPassCompletionShare * 100f:F0}%)");
        Console.WriteLine($"Shots Attempted   : Home {a.HomeShots} | Away {a.AwayShots}");
        Console.WriteLine($"Mean Team Width   : Home {a.HomeAverageTeamWidthMeters:F1}m | Away {a.AwayAverageTeamWidthMeters:F1}m");
        Console.WriteLine($"Kicks (execution) : {executedKicks} executed / {requestedKicks} requested | refused "
                          + (refusalReasons.Count == 0 ? "none" : string.Join(", ", refusalReasons)));
        Console.WriteLine($"Run adverts (A5.3): {engine.Communication.Runs.PublishedCount} published | "
                          + $"{engine.Communication.Runs.WithdrawnCount} withdrawn | "
                          + $"{engine.Communication.Runs.ClaimedCount} claims by a carrier");
        Console.WriteLine($"Airborne (A5.5)   : {crosses} crosses | {lofted} lofted | {driven} driven executed");
        Console.WriteLine($"Events Logged     : {engine.Rules.Events.Count}");

        return completed;
    }

    /// <summary>
    /// Multi-seed tactical report and gate (audit F23).
    ///
    /// The report is an assertion: every seed must complete, and the possession profile must be the
    /// wider shape across the seed set (mean difference, majority of seeds). A single chaotic match is
    /// allowed to invert, which the report shows per seed.
    ///
    /// The comparison uses the possession-scoped span (A4): the profile's width is a statement about the
    /// shape a team holds *with the ball*, and the unconditional span mostly reported who was attacking.
    /// Both numbers are printed, so the effect can be read either way.
    /// </summary>
    private static bool RunTacticalComparisonBatch()
    {
        Console.WriteLine("=== Phase 6 & 9 Multi-Seed Tactical Differentiation Batch (Possession vs Direct Counter) ===");
        // AGENTS §2.2: every recorded artifact states the simulation version, and only same-version numbers
        // are comparable. The match and perf modes already do; the gate artifact did not until A6.1.
        Console.WriteLine($"Simulation version: {SimulationVersion.Tag}");
        const float halfSeconds = 120f;
        bool allPassed = true;
        float widthDifferenceSum = 0f;
        int widerSeeds = 0;
        const int seedCount = 5;

        for (ulong index = 1; index <= seedCount; index++)
        {
            var engine = new MatchSimulationEngine(new PreMatchConfig
            {
                Seed = 5000UL + index,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack
            });

            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
                engine.StepTick();

            var a = engine.Analytics;
            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            bool played = a.HomePassesAttempted > 0 && a.AwayPassesAttempted > 0;
            bool wider = a.HomeAttackingWidthMeters > a.AwayAttackingWidthMeters;
            widthDifferenceSum += a.HomeAttackingWidthMeters - a.AwayAttackingWidthMeters;
            if (wider) widerSeeds++;
            allPassed &= completed && played;

            Console.WriteLine(
                $"Seed {5000UL + index}: {(completed ? "FT" : "INCOMPLETE")} " +
                $"clock {engine.Rules.MatchClockSeconds:F0}s | Score {a.HomeGoals}-{a.AwayGoals} | " +
                $"Passes {a.HomePassesAttempted} vs {a.AwayPassesAttempted} " +
                $"(completed {a.HomePassesCompleted} vs {a.AwayPassesCompleted}) | " +
                $"Shots {a.HomeShots} vs {a.AwayShots} | " +
                $"Width {a.HomeAttackingWidthMeters:F1}m vs {a.AwayAttackingWidthMeters:F1}m " +
                $"({(wider ? "wider" : "narrower")}; all-tick {a.HomeAverageTeamWidthMeters:F1} vs " +
                $"{a.AwayAverageTeamWidthMeters:F1}) | " +
                $"Possession {a.HomePossessionShare * 100f:F1}% | Played={(played ? "yes" : "no")}");
        }

        float meanWidthDifference = widthDifferenceSum / seedCount;
        bool separationPassed = widerSeeds >= seedCount - 1 && meanWidthDifference > 0.5f;
        allPassed &= separationPassed;

        Console.WriteLine(
            $"Width separation : mean {meanWidthDifference:F2} m (with the ball), wider on {widerSeeds}/{seedCount} seeds " +
            $"(gate: majority and mean > 0.5 m)");
        Console.WriteLine(allPassed ? "GATE: PASS" : "GATE: FAIL");
        return allPassed;
    }
}
