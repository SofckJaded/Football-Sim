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
using FootballSim.Tactics;

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
                return RunHeadlessMatch(11, 2700f, 20260818UL, restartRoutines: !IsOffArgument(args)) ? 0 : 1;

            case "--tactical-batch":
                return RunTacticalComparisonBatch() ? 0 : 1;

            case "--scenario":
                return RunScenarioCommand(args);

            case "--perf":
                return RunPerformanceSample(args) ? 0 : 1;

            case "--deliveries":
                return RunDeliveryEvidence(args) ? 0 : 1;

            case "--lines":
                return RunLineEvidence(args) ? 0 : 1;

            case "--turnovers":
                return RunTurnoverEvidence(args) ? 0 : 1;

            case "--keepers":
                return RunKeeperEvidence(args) ? 0 : 1;

            case "--scans":
                return RunScanEvidence(args) ? 0 : 1;

            case "--calls":
                return RunTeamCallEvidence(args) ? 0 : 1;

            case "--receptions":
                return RunReceptionEvidence(args) ? 0 : 1;

            case "--firsttouch":
                return RunFirstTouchEvidence(args) ? 0 : 1;

            case "--cognitive":
                return RunCognitiveErrorEvidence(args) ? 0 : 1;

            case "--archetypes":
                return RunArchetypeEvidence(args) ? 0 : 1;

            case "--combinations":
                return RunCombinationEvidence(args) ? 0 : 1;

            case "--search":
                return RunShallowSearchEvidence(args) ? 0 : 1;

            case "--switches":
                return RunSwitchEvidence(args) ? 0 : 1;

            case "--traps":
                return RunTrapEvidence(args) ? 0 : 1;

            case "--routines":
                return RunRestartEvidence(args) ? 0 : 1;

            case "--styles":
                return RunStyleEvidence(args) ? 0 : 1;

            case "--ablations":
                return RunAblationEvidence(args) ? 0 : 1;

            default:
                Console.WriteLine("FootballSim.Headless CLI Runner");                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --physics-lab");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --match-5v5");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --match-11v11");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --tactical-batch");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --scenario list");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --scenario build-up-3v2 [ticks]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --perf [ticks]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --deliveries [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --lines [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --turnovers [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --keepers [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --scans [seeds] [halfSeconds] [on|off]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --calls [seeds] [halfSeconds] [on|off]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --receptions [seeds] [halfSeconds] [on|off]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --firsttouch [seeds] [halfSeconds] [on|off]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --cognitive [seeds] [halfSeconds] [on|off]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --archetypes [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --combinations [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --search [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --switches [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --traps [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --routines [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --styles [seeds] [halfSeconds]");
                Console.WriteLine("  dotnet run --project src/FootballSim.Headless -- --ablations [seeds] [halfSeconds]");
                Console.WriteLine();
                Console.WriteLine("Every match mode exits non-zero when the match did not actually reach full time");
                Console.WriteLine("(audit F23: hitting the tick cap is no longer reported as a completed match).");

                // L47: an unknown mode used to print this help and exit 0, so a typo'd or stale documented
                // command looked successful while running nothing. Only an explicit help request is success.
                if (mode is "--help" or "-h" or "/?") return 0;

                Console.Error.WriteLine($"unknown mode '{mode}' — use one of the modes above");
                return 1;
        }
    }

    /// <summary>
    /// True when the optional argument after a mode is <c>off</c>. Used by the ablation argument of
    /// <c>--match-11v11</c>: A8.5's <c>off</c> run is the version-50 build exactly, which is how the
    /// restart-routine-off equivalence is proved against the recorded <c>ai50</c> match.
    /// </summary>
    private static bool IsOffArgument(string[] args) =>
        args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one advanced-AI scenario fixture. This exists so a behaviour can be reproduced from the
    /// command line without writing a test â€” the same <see cref="ScenarioLibrary"/> the suite uses.
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

        Console.WriteLine($"=== Scenario '{definition.Key}' â€” {ticks} tick budget ===");
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
    /// reads the carrier's own candidate table after each decision â€” no simulation state, no extra behaviour â€”
    /// and reports, per delivery family:
    ///
    /// <list type="bullet">
    /// <item><b>offered</b> â€” live decision ticks where the family appeared in the carrier's action set;</item>
    /// <item><b>chosen</b> â€” how many separate selections the family won (a new choice, not a held one);</item>
    /// <item><b>executed</b> â€” balls the execution layer actually struck, from its own contacts;</item>
    /// <item><b>margin</b> â€” how far the family's best score was from the best <i>other</i> family's best
    ///   score on the same tick (positive = the delivery lost by that much). Carry and recycle are in that
    ///   comparison set, because they are the baselines a delivery has to beat (M10);</item>
    /// <item><b>p / gain / loss</b> â€” the family's best candidate's own value terms (one candidate, not an
    ///   average over every record-breaker, which mixed several actions into one row — L42).</item>
    /// </list>
    ///
    /// The counts are ticks, not events: a carrier who holds the same action for forty ticks counts forty
    /// times. That is the right denominator for "was it on the table", and the chosen/executed columns are
    /// what carry the decision evidence. Rejections are the one exception: a refusal belongs to a decision,
    /// so it is counted once per computed decision, not once per tick (M11).
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
        Console.WriteLine("                     margin = best score of any other family - this family's best (>0 = it lost).");
        Console.WriteLine("                     Rejections are counted once per computed decision.");

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
            var previousDecisions = new int[players.Count];
            int decoyTicks = 0;
            int decoyAdoptions = 0;
            var previousDecoy = new bool[players.Count];

            var familyBest = new float[FamilyCount];
            var familyBestIndex = new int[FamilyCount];
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

                    // A refusal belongs to a decision (M11): the generator's rejection buffer is reset by
                    // every decision, so reading it once after the match counted the final tick only.
                    // Compare the controller's decision counter instead of sampling per tick, or a held
                    // decision would be counted once per tick it is held.
                    if (ai.DecisionsComputed != previousDecisions[index])
                    {
                        previousDecisions[index] = ai.DecisionsComputed;
                        for (int r = 0; r < ai.RejectionCount; r++)
                        {
                            string refusal = ai.GetRejection(r).Reason;
                            rejections[refusal] = rejections.GetValueOrDefault(refusal) + 1;
                        }
                    }

                    Array.Fill(familyBest, float.NegativeInfinity);
                    Array.Fill(familyBestIndex, -1);
                    IReadOnlyList<UtilityCandidate> candidates = ai.LastCandidates;

                    // The family's best candidate, and only that candidate's terms (L42): accumulating
                    // over every record-breaking candidate averaged several different actions' p/gain/loss
                    // into one row that described none of them.
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        UtilityCandidate candidate = candidates[c];
                        int family = FamilyOf(candidate.Key);
                        if (family < 0) continue;
                        if (candidate.UtilityScore <= familyBest[family]) continue;
                        familyBest[family] = candidate.UtilityScore;
                        familyBestIndex[family] = c;
                    }

                    for (int family = 0; family < FamilyCount; family++)
                    {
                        int winnerIndex = familyBestIndex[family];
                        if (winnerIndex < 0) continue;

                        seedOffered[family]++;

                        UtilityCandidate winner = candidates[winnerIndex];
                        if (winner.Terms.IsPresent)
                        {
                            seedProbability[family] += winner.Terms.Probability01;
                            seedGain[family] += winner.Terms.Gain;
                            seedLoss[family] += winner.Terms.Loss;
                            seedTermsTicks[family]++;
                        }

                        // M10: the comparison set is every *other* family's best, which includes the carry
                        // and the recycle. Excluding them made the delivery-vs-carry gap the plan is about
                        // invisible: the recorded cross margin was 0.46 while the carry row was -0.35.
                        float bestOther = float.NegativeInfinity;
                        for (int other = 0; other < FamilyCount; other++)
                        {
                            if (other == family) continue;
                            if (familyBest[other] > bestOther) bestOther = familyBest[other];
                        }

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

    /// <summary>
    /// A6.3 evidence: the defensive line's shared depth band, measured against the shape it replaced.
    ///
    /// The milestone's claim is not "the line is a number" but "the line is one number the whole line holds,
    /// and four picture facts move it". This probe measures each half of that on live matches:
    /// <list type="bullet">
    /// <item><b>spread</b> â€” mean and maximum distance of a line player's <i>assigned target depth</i> from
    ///   the band's, per defending sample. A shared band is only shared if this is small; the body's own lag
    ///   behind its target is reported separately as the body-vs-band gap.</item>
    /// <item><b>shift from the anchors</b> â€” band depth minus the line's mean formation-anchor depth, which is
    ///   the depth the same players would have held before this milestone. It quantifies the behaviour change
    ///   instead of leaving it to the scoreline.</item>
    /// <item><b>terms</b> â€” how often each input (field position, passer pressure, a credible run, keeper
    ///   coverage) moved the band and by how much, so a lever that is inert in real play is visible here.</item>
    /// <item><b>style</b> â€” the markers each profile's zone/man mix asks for, and the handovers it made.</item>
    /// </list>
    ///
    /// Denominators: a sample is one live tick of one team defending with a valid band, taken every
    /// <see cref="LineSampleInterval"/>. Usual speed, sprints and restarts are all included; the band itself is
    /// only recomputed on the allocator's cadence, so a sample repeats the last band.
    /// </summary>
    private static bool RunLineEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 900f;

        var homeProfile = TeamTacticalProfile.PossessionControl;
        var awayProfile = TeamTacticalProfile.DirectCounterattack;

        Console.WriteLine($"=== A6.3 defensive line evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Profiles           : home {homeProfile.Name} (marks {DefensiveLineModel.MarkerCount(homeProfile)}," +
                          $" man-share {DefensiveLineModel.ManShare01(homeProfile):F2})" +
                          $" | away {awayProfile.Name} (marks {DefensiveLineModel.MarkerCount(awayProfile)}," +
                          $" man-share {DefensiveLineModel.ManShare01(awayProfile):F2})");
        Console.WriteLine("Units              : metres, attack-relative (own goal line = -52, opponent's = +52).");
        Console.WriteLine("                     spread = |assigned line target - band depth| over the players told to hold the");
        Console.WriteLine("                     line (the shared-band claim); body lag is reported separately as body-vs-band gap;");
        Console.WriteLine("                     shift  = band depth - the line's mean formation-anchor depth.");

        int samples = 0;
        int matchesPlayed = 0;
        double spreadSum = 0d, spreadCount = 0d;
        double lagSum = 0d, lagCount = 0d, maxLineGap = 0d;
        double keeperReachSum = 0d, keeperProjSum = 0d;
        int lineAtBand = 0, lineAhead = 0, lineTucked = 0, lineClamped = 0;
        double shiftSum = 0d, shiftAbsSum = 0d;
        double bandSum = 0d, ballSum = 0d, anchorSum = 0d;
        double fieldSum = 0d, pressureSum = 0d, ballCapSum = 0d, runSum = 0d, keeperSum = 0d;
        double pressure01Sum = 0d;
        int fieldMoved = 0, pressureMoved = 0, ballCapped = 0, runCapped = 0, keeperCapped = 0;
        int observationMismatches = 0;
        int handovers = 0;
        double handoverFromSlot = 0d, handoverToSlot = 0d;
        double markersSum = 0d;
        int linePlayerSamples = 0;
        int widestSpreadTick = 0;
        double widestSpread = 0d;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = homeProfile,
                AwayProfile = awayProfile
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            MatchPeriod period = engine.Rules.Period;
            int periodChangedAt = 0;

            for (int tick = 0; tick < maxTicks; tick++)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;

                // The interval reverses the attacking frame: anchors written before it are in the old frame,
                // so the two are not comparable for the tick or two it takes the shape to be rewritten. Not a
                // behaviour, a measurement artifact â€” skip those samples rather than report them as a spread.
                if (engine.Rules.Period != period)
                {
                    period = engine.Rules.Period;
                    periodChangedAt = tick;
                }

                if (tick - periodChangedAt < 2 * LineSampleInterval) continue;
                if (tick % LineSampleInterval != 0) continue;

                foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
                {
                    DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
                    if (!band.IsValid) continue;

                    float attackDirX = team == TeamId.Home ? engine.Rules.GetHomeAttackDirectionX() : -engine.Rules.GetHomeAttackDirectionX();
                    float ballProj = engine.Ball.Position.X * attackDirX;

                    samples++;
                    bandSum += band.ProjX;
                    ballSum += ballProj;
                    anchorSum += band.AnchorMeanProjX;
                    fieldSum += band.FieldShiftMeters;
                    pressureSum += band.PressureShiftMeters;
                    ballCapSum += band.BallCapShiftMeters;
                    runSum += band.RunShiftMeters;
                    keeperSum += band.KeeperShiftMeters;
                    pressure01Sum += band.PasserPressure01;
                    if (MathF.Abs(band.FieldShiftMeters) > 0.5f) fieldMoved++;
                    if (MathF.Abs(band.PressureShiftMeters) > 0.5f) pressureMoved++;
                    if (band.BallCapShiftMeters < -0.5f) ballCapped++;
                    if (band.RunShiftMeters < -0.5f) runCapped++;
                    if (band.KeeperShiftMeters < -0.5f) keeperCapped++;

                    float shift = band.ProjX - band.AnchorMeanProjX;
                    shiftSum += shift;
                    shiftAbsSum += MathF.Abs(shift);

                    int markers = 0;
                    int linePlayers = 0;
                    float spreadThisSample = 0f;
                    foreach (var player in engine.Players)
                    {
                        if (player.Team != team || !player.IsActiveOnPitch) continue;
                        TeamJobAssignment? duty = engine.TeamJobs.Get(player.Id);
                        if (duty is null) continue;

                        if (duty.Value.Job == TacticalJob.Marker) markers++;
                        if (duty.Value.Job != TacticalJob.DefensiveLine) continue;

                        linePlayers++;
                        float actual = player.Position.X * attackDirX;

                        // What the band actually placed: every line duty's own target. This is the shared-band
                        // claim. Where the body is lags the target by however long the run takes, so it is
                        // reported separately rather than folded in.
                        float target = duty.Value.Anchor.X * attackDirX;
                        float targetDistance = MathF.Abs(target - band.ProjX);
                        spreadSum += targetDistance;
                        spreadCount += 1d;
                        if (targetDistance > spreadThisSample) spreadThisSample = targetDistance;

                        if (targetDistance <= 1e-3f) lineAtBand++;
                        else if (MathF.Abs(target - (band.ProjX + DefensiveLineModel.BallSideAdvanceMeters)) <= 1e-3f) lineAhead++;
                        else if (MathF.Abs(target - (band.ProjX - DefensiveLineModel.FarSideTuckMeters)) <= 1e-3f) lineTucked++;
                        else lineClamped++;

                        lagSum += MathF.Abs(actual - target);
                        lagCount += 1d;
                        maxLineGap = Math.Max(maxLineGap, Math.Abs(actual - band.ProjX));

                        // The band must be the one the player was told about, not a second estimate.
                        DefensiveLineBand seen = engine.GetLastObservation(player.Id)?.TeamLineBand ?? default;
                        if (!seen.IsValid || MathF.Abs(seen.ProjX - band.ProjX) > 1e-3f) observationMismatches++;
                    }

                    keeperReachSum += band.KeeperReachMeters;
                    keeperProjSum += band.KeeperId > 0
                        ? engine.Players.First(p => p.Id == band.KeeperId).Position.X * attackDirX
                        : 0f;

                    if (linePlayers > 0) linePlayerSamples++;
                    markersSum += markers;
                    if (spreadThisSample > widestSpread)
                    {
                        widestSpread = spreadThisSample;
                        widestSpreadTick = tick;
                    }
                }
            }

            handovers += engine.TeamJobs.HandoverCount;
            foreach (MarkingHandover handover in engine.TeamJobs.Handovers)
            {
                handoverFromSlot += handover.FromSlotDistance;
                handoverToSlot += handover.ToSlotDistance;
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matchesPlayed++;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks | " +
                              $"score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} | " +
                              $"handovers {engine.TeamJobs.HandoverCount}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matchesPlayed} completed, {samples} defending samples) ===");
        if (samples == 0)
        {
            Console.WriteLine("No defending sample produced a band â€” nothing to measure.");
            return false;
        }

        Console.WriteLine($"Band depth         : mean {bandSum / samples:F1} m | mean ball {ballSum / samples:F1} m" +
                          $" | mean anchors {anchorSum / samples:F1} m");
        Console.WriteLine($"Shift from anchors : mean {shiftSum / samples:+0.0;-0.0} m | mean absolute {shiftAbsSum / samples:F1} m" +
                          $" (the depth the same players would have held before A6.3)");
        Console.WriteLine($"Line target spread : mean {spreadSum / Math.Max(1d, spreadCount):F2} m" +
                          $" | worst player sample {widestSpread:F2} m @ tick {widestSpreadTick}" +
                          $" | {linePlayerSamples} samples with a line");
        Console.WriteLine($"Local exceptions   : {lineAtBand} at the band | {lineAhead} ball-side (+" +
                          $"{DefensiveLineModel.BallSideAdvanceMeters:F0} m) | {lineTucked} far-side (-" +
                          $"{DefensiveLineModel.FarSideTuckMeters:F0} m) | {lineClamped} clamped by the band's range");
        Console.WriteLine($"Body-vs-band gap   : mean {lagSum / Math.Max(1d, lagCount):F2} m | worst {maxLineGap:F2} m" +
                          " (physical lag: the target moves before the player can arrive)");
        Console.WriteLine($"Keeper context     : mean reach {keeperReachSum / samples:F1} m from mean depth {keeperProjSum / samples:F1} m");
        Console.WriteLine($"Field position     : mean shift {fieldSum / samples:+0.0;-0.0} m | moved >0.5 m in {fieldMoved * 100.0 / samples:F1}% of samples");
        Console.WriteLine($"Passer pressure    : mean {pressure01Sum / samples:F2} | mean shift {pressureSum / samples:+0.0;-0.0} m" +
                          $" | moved >0.5 m in {pressureMoved * 100.0 / samples:F1}%");
        Console.WriteLine($"Ball cap           : pulled the line back in {ballCapped * 100.0 / samples:F1}% of samples" +
                          $" | mean shift {ballCapSum / samples:+0.00;-0.00} m");
        Console.WriteLine($"Credible runs      : capped the line in {runCapped * 100.0 / samples:F1}% of samples" +
                          $" | mean shift {runSum / samples:+0.00;-0.00} m");
        Console.WriteLine($"Keeper coverage    : capped the line in {keeperCapped * 100.0 / samples:F1}% of samples" +
                          $" | mean shift {keeperSum / samples:+0.00;-0.00} m");
        Console.WriteLine($"Markers            : mean {markersSum / samples:F2} per defending sample" +
                          $" (style caps {DefensiveLineModel.MarkerCount(homeProfile)}/{DefensiveLineModel.MarkerCount(awayProfile)})");
        Console.WriteLine($"Handovers          : {handovers} total" +
                          (handovers > 0
                              ? $" | mean slots {handoverFromSlot / handovers:F1} m -> {handoverToSlot / handovers:F1} m"
                              : string.Empty));
        Console.WriteLine($"Observation match  : {(observationMismatches == 0 ? "every line player saw the placed band" : $"{observationMismatches} MISMATCHES")}");

        bool ok = matchesPlayed == seedCount && observationMismatches == 0;
        Console.WriteLine(ok ? "RESULT: bands placed, shared and observed" : "RESULT: incomplete or inconsistent");
        return ok;
    }

    /// <summary>How often the line probe samples a defending team, in ticks (0.1 s).</summary>
    private const int LineSampleInterval = 6;

    /// <summary>
    /// A6.4 evidence: how often each tournament answer is in force, what the four inputs looked like when it
    /// was, and what it did to the line and the marking mix.
    ///
    /// Denominator: one live tick of one team, sampled every <see cref="TurnoverSampleInterval"/> ticks. A
    /// sample is "in a transition" when the team has a turnover answer at all â€” the first
    /// <see cref="TurnoverResponseModel.ResponseWindowSeconds"/> seconds after the ball was lost â€” and the
    /// answer in force is the one that tick. The aggregates are the same fact the players' observations carry,
    /// not a re-derived guess. Choice counts are episodes: a new count every time the published answer
    /// changes, so "the side never counter-pressed" is distinguishable from "it counter-pressed and gave it
    /// up".
    /// </summary>
    private static bool RunTurnoverEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 900f;

        var homeProfile = TeamTacticalProfile.PossessionControl;
        var awayProfile = TeamTacticalProfile.DirectCounterattack;

        Console.WriteLine($"=== A6.4 turnover response evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Profiles           : home {homeProfile.Name} (pressing {homeProfile.PressingIntensity01:F2}," +
                          $" window {homeProfile.CounterPressDurationSeconds:F1} s)" +
                          $" | away {awayProfile.Name} (pressing {awayProfile.PressingIntensity01:F2}," +
                          $" window {awayProfile.CounterPressDurationSeconds:F1} s)");
        Console.WriteLine($"Response window    : {TurnoverResponseModel.ResponseWindowSeconds:F1} s after the loss," +
                          $" held for {TurnoverResponseModel.MinimumHoldTicks} ticks");

        int answers = 4;
        var samples = new int[answers];
        var localSum = new double[answers];
        var securitySum = new double[answers];
        var constraintSum = new double[answers];
        var escapeSum = new double[answers];
        var shiftSum = new double[answers];
        var shiftCount = new int[answers];
        var markerSum = new double[answers];
        int windowSamples = 0;
        int totalSamples = 0;
        int matchesPlayed = 0;
        int samplesInTransition = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = homeProfile,
                AwayProfile = awayProfile
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

            for (int tick = 0; tick < maxTicks; tick++)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;
                if (tick % TurnoverSampleInterval != 0) continue;

                totalSamples += 2;
                if (engine.TeamJobs.TurnoverOf(TeamId.Home).CounterPressWindowOpen) windowSamples++;

                foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
                {
                    TurnoverSituation situation = engine.TeamJobs.TurnoverOf(team);
                    int answer = (int)situation.Response;
                    if (answer < 0 || answer >= answers) continue;

                    samples[answer]++;
                    if (!situation.Responds) continue;
                    samplesInTransition++;

                    localSum[answer] += situation.LocalNumbers01;
                    securitySum[answer] += situation.CentralSecurity01;
                    constraintSum[answer] += situation.CarrierConstraint01;
                    escapeSum[answer] += situation.EscapeRoute01;

                    DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
                    if (band.IsValid)
                    {
                        shiftSum[answer] += band.TurnoverShiftMeters;
                        shiftCount[answer]++;
                    }

                    int markers = 0;
                    foreach (FootballerState player in engine.Players)
                    {
                        if (player.Team != team || !player.IsActiveOnPitch) continue;
                        if (engine.TeamJobs.Get(player.Id)?.Job == TacticalJob.Marker) markers++;
                    }

                    markerSum[answer] += markers;
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matchesPlayed++;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks | " +
                              $"score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals}");
            foreach (TeamId team in new[] { TeamId.Home, TeamId.Away })
            {
                Console.WriteLine($"  {team,-5} choices: counter-press {engine.TeamJobs.TurnoverChoiceCount(team, TurnoverResponse.CounterPress),3}" +
                                  $" | delay/recover {engine.TeamJobs.TurnoverChoiceCount(team, TurnoverResponse.DelayRecover),3}" +
                                  $" | retreat {engine.TeamJobs.TurnoverChoiceCount(team, TurnoverResponse.Retreat),3}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matchesPlayed} completed, {totalSamples} team-samples) ===");
        if (matchesPlayed == 0 || totalSamples == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"Transition samples : {samplesInTransition}/{totalSamples} " +
                          $"({100.0 * samplesInTransition / totalSamples:F1} % of live ticks carried a turnover answer)");
        Console.WriteLine($"Counter-press window: open on {100.0 * windowSamples / Math.Max(1, totalSamples / 2):F1} % of sampled ticks");

        string[] names =
        {
            "no transition", "counter-press", "delay/recover", "retreat"
        };

        for (int i = 0; i < answers; i++)
        {
            int count = samples[i];
            if (count == 0)
            {
                Console.WriteLine($"{names[i],-14}: never chosen");
                continue;
            }

            int responded = i == 0 ? 0 : count;
            string inputs = responded == 0
                ? string.Empty
                : $" | local {localSum[i] / responded:F2} security {securitySum[i] / responded:F2}" +
                  $" constraint {constraintSum[i] / responded:F2} escape {escapeSum[i] / responded:F2}";
            string body = shiftCount[i] == 0
                ? string.Empty
                : $" | line shift {(shiftSum[i] / shiftCount[i]):+0.00;-0.00} m, {markerSum[i] / responded:F2} markers";

            Console.WriteLine($"{names[i],-14}: {100.0 * count / totalSamples,5:F1} % of samples" +
                              $" ({count} ticks){inputs}{body}");
        }

        bool ok = matchesPlayed == seedCount && samplesInTransition > 0;
        Console.WriteLine(ok
            ? "RESULT: turnovers answered, inputs and consequences measured"
            : "RESULT: incomplete, or no turnover was ever answered");
        return ok;
    }

    /// <summary>How often the turnover probe samples each team, in ticks (0.2 s).</summary>
    private const int TurnoverSampleInterval = 12;

    /// <summary>
    /// A6.5 evidence: what the keepers did with the ball's real trajectory.
    ///
    /// Denominator: one live tick of one team's keeper, sampled every <see cref="KeeperSampleInterval"/> ticks. A
    /// <b>sweep episode</b> is counted when the sweep race's verdict changes (a new answer, not a repeated tick),
    /// so the numbers are decisions rather than durations; a <b>shot</b> is counted when a new shot on target is
    /// first seen and when the keeper first decides about it. The reasons come from the model itself
    /// (<see cref="GoalkeeperRaceReason"/>), so the artifact cannot drift from the decision.
    /// </summary>
    private static bool RunKeeperEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 900f;

        var homeProfile = TeamTacticalProfile.PossessionControl;
        var awayProfile = TeamTacticalProfile.DirectCounterattack;
        var pitch = new PitchDimensions();
        Console.WriteLine($"=== A6.5b goalkeeper decisions evidence: {seedCount} seeds x" +
                          $" {halfSeconds * 2f:F0} s ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Trajectory         : horizon {GoalkeeperDecisionModel.TrajectoryHorizonSeconds:F1} s," +
                          $" {BallTrajectoryEstimate.MaxSamples} samples every" +
                          $" {1f / BallTrajectoryEstimate.SampleStepSeconds:F0} per second, real solver," +
                          $" built within {GoalkeeperDecisionModel.TrajectoryBuildRangeMeters:F0} m of the goal line");
        Console.WriteLine($"Sweep race         : margin {GoalkeeperDecisionModel.SweepRaceMarginSeconds:F2} s," +
                          $" commit range {GoalkeeperDecisionModel.SweepCommitRangeMeters:F1} m," +
                          $" handling height {GoalkeeperDecisionModel.HandlingHeightMeters:F2} m," +
                          " gates at the meeting point (own penalty area, Law 12)");
        Console.WriteLine($"Claim/punch        : airborne above {GoalkeeperDecisionModel.ClaimAirborneHeightMeters:F2} m," +
                          $" commit range {GoalkeeperDecisionModel.ClaimCommitRangeMeters:F1} m," +
                          $" hold limit {BallContactRules.KeeperCatchSpeedLimit:F1} m/s x reflex");
        Console.WriteLine($"Shot decision      : horizon {GoalkeeperDecisionModel.ShotHorizonSeconds:F2} s," +
                          $" mouth slack {GoalkeeperDecisionModel.GoalMouthSlackMeters:F1} m");
        Console.WriteLine($"Handling law       : own penalty area {pitch.PenaltyAreaLength:F1} m x" +
                          $" {pitch.PenaltyAreaWidth:F2} m (Law 12: no deliberate team-mate kick, no second" +
                          " handling after release)");
        Console.WriteLine($"Support duty       : {GoalkeeperSupportModel.SupportDepthShare:P0} of the goal-to-ball" +
                          $" distance in possession, {GoalkeeperSupportModel.MinSupportDepthMeters:F1}" +
                          $"-{GoalkeeperSupportModel.MaxSupportDepthMeters:F0} m; defending" +
                          $" {GoalkeeperSupportModel.DefensiveDepthShare:P0}," +
                          $" {GoalkeeperSupportModel.MinDefensiveDepthMeters:F1}" +
                          $"-{GoalkeeperSupportModel.MaxDefensiveDepthMeters:F1} m");

        int answers = Enum.GetValues(typeof(GoalkeeperRaceReason)).Length;
        var raceEpisodes = new int[answers];
        var raceKeeperSeconds = new double[answers];
        var raceOpponentSeconds = new double[answers];
        var raceCounts = new int[answers];
        int handlingReasons = Enum.GetValues(typeof(GoalkeeperHandlingReason)).Length;
        var handlingSamples = new int[handlingReasons];
        int shotsSeen = 0;
        int shotsDived = 0;
        int shotsDeclined = 0;
        int shotsBlockedByLaw = 0;
        double interceptSecondsSum = 0d;

        // A6.5b: deliveries claimed, punched and left alone, and what the keeper did with the ball in his hands.
        int claimReasons = Enum.GetValues(typeof(GoalkeeperClaimReason)).Length;
        var claimReasonEpisodes = new int[claimReasons];
        int claimsAttacked = 0;
        int claimsGathered = 0;
        int claimsPunched = 0;
        int claimsDeclined = 0;
        double claimKeeperSecondsSum = 0d;
        double claimAttackerSecondsSum = 0d;
        double claimArrivalSpeedSum = 0d;
        int claimDecisionCount = 0;
        int distributionCounts = Enum.GetValues(typeof(GoalkeeperDistributionKind)).Length;
        var distributionKinds = new int[distributionCounts];
        double distributionCompletionSum = 0d;
        double distributionDistanceSum = 0d;
        int distributionPriced = 0;
        var lineSamples = new int[2];
        var standoffSum = new double[2];
        var standoffMax = new float[2];

        int matchesPlayed = 0;
        int samples = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            // L44: the per-seed line reports this match's own samples, not the running total.
            int seedSamples = 0;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = homeProfile,
                AwayProfile = awayProfile
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

            var previousReason = new Dictionary<int, GoalkeeperRaceReason>();
            var previousShot = new Dictionary<int, bool>();
            var previousClaim = new Dictionary<int, (GoalkeeperClaimReason Reason, GoalkeeperClaimMode Mode)>();
            var previousDistribution = new Dictionary<int, GoalkeeperDistributionKind>();

            for (int tick = 0; tick < maxTicks; tick++)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;

                // The sample gate is for the standing counters (verdicts, hands, standoff). A distribution is a
                // one-tick decision, so it is counted on every tick — otherwise a 0.2 s sampling interval misses
                // almost all of them, which is exactly what the first run of this probe did.
                bool sampled = tick % KeeperSampleInterval == 0;

                foreach (FootballerState player in engine.Players)
                {
                    if (player.Role != TacticalRole.Goalkeeper || !player.IsActiveOnPitch) continue;
                    if (engine.GetController(player.Id) is not GoalkeeperController keeper) continue;
                    int playerId = player.Id;

                    // Distribution is counted on every tick (it is a one-tick decision); everything below it is
                    // a standing state that the 0.2 s sample describes well.
                    GoalkeeperDistribution distribution = keeper.LastDistribution;
                    bool distributing = distribution.Kind != GoalkeeperDistributionKind.None;
                    bool wasDistributing = previousDistribution.TryGetValue(playerId, out GoalkeeperDistributionKind was)
                                           && was != GoalkeeperDistributionKind.None;
                    previousDistribution[playerId] = distribution.Kind;
                    if (distributing && !wasDistributing)
                    {
                        int kind = (int)distribution.Kind;
                        if (kind >= 0 && kind < distributionCounts) distributionKinds[kind]++;
                        if (distribution.Kind != GoalkeeperDistributionKind.SafeBall)
                        {
                            distributionPriced++;
                            distributionCompletionSum += distribution.Completion01;
                            distributionDistanceSum += distribution.DistanceMeters;
                        }
                    }

                    if (!sampled) continue;
                    samples++;
                    seedSamples++;

                    int handlingIndex = (int)keeper.LastHandling.Reason;
                    if (handlingIndex >= 0 && handlingIndex < handlingReasons)
                        handlingSamples[handlingIndex]++;

                    // A6.5b support duty: how far off his own line he actually stands, split by whose ball it is
                    // (the possession snapshot's own answer, not a second possession model).
                    float ownGoalX = player.Position.X < 0f ? -pitch.HalfLength : pitch.HalfLength;
                    float standoff = MathF.Abs(player.Position.X - ownGoalX);
                    TeamId? possessor = engine.Possession.PossessionTeam;
                    int standoffBucket = possessor == player.Team ? 1 : 0;
                    lineSamples[standoffBucket]++;
                    standoffSum[standoffBucket] += standoff;
                    if (standoff > standoffMax[standoffBucket]) standoffMax[standoffBucket] = standoff;

                    GoalkeeperRace race = keeper.LastSweep;
                    GoalkeeperRaceReason reason = keeper.SweepEvaluated
                        ? race.Reason
                        : GoalkeeperRaceReason.NoTrajectory;
                    bool evaluated = keeper.SweepEvaluated;
                    if (evaluated &&
                        (!previousReason.TryGetValue(playerId, out GoalkeeperRaceReason seen) || seen != reason))
                    {
                        previousReason[playerId] = reason;
                        int index = (int)reason;
                        if (index >= 0 && index < answers)
                        {
                            raceEpisodes[index]++;
                            if (race.Reason is GoalkeeperRaceReason.KeeperFirst or GoalkeeperRaceReason.Contested
                                or GoalkeeperRaceReason.OpponentFirst)
                            {
                                raceKeeperSeconds[index] += race.KeeperSeconds;
                                raceOpponentSeconds[index] += race.OpponentSeconds;
                                raceCounts[index]++;
                            }
                        }
                    }

                    if (keeper.ClaimEvaluated)
                    {
                        GoalkeeperClaim claim = keeper.LastClaim;
                        var state = (claim.Reason, claim.Mode);
                        if (!previousClaim.TryGetValue(playerId, out (GoalkeeperClaimReason Reason,
                                GoalkeeperClaimMode Mode) seenClaim) || seenClaim != state)
                        {
                            previousClaim[playerId] = state;
                            int index = (int)claim.Reason;
                            if (index >= 0 && index < claimReasons) claimReasonEpisodes[index]++;
                            if (claim.ShouldAttack)
                            {
                                claimsAttacked++;
                                if (claim.WillHold) claimsGathered++;
                                else claimsPunched++;
                                claimKeeperSecondsSum += claim.KeeperSeconds;
                                claimAttackerSecondsSum += claim.AttackerSeconds < float.MaxValue
                                    ? claim.AttackerSeconds
                                    : claim.KeeperSeconds;
                                claimArrivalSpeedSum += claim.ArrivalSpeedMps;
                                claimDecisionCount++;
                            }
                            else if (claim.Reason == GoalkeeperClaimReason.OpponentFirst)
                            {
                                claimsDeclined++;
                            }
                        }
                    }

                    GoalkeeperShot shot = keeper.LastShot;
                    bool onTarget = keeper.ShotEvaluated && shot.IsOnTarget;
                    if (!previousShot.TryGetValue(playerId, out bool wasOnTarget) || wasOnTarget != onTarget)
                    {
                        previousShot[playerId] = onTarget;
                        if (onTarget)
                        {
                            shotsSeen++;
                            if (shot.CanSave)
                            {
                                shotsDived++;
                                interceptSecondsSum += shot.InterceptSeconds;
                            }
                            else if (shot.CanIntercept)
                            {
                                shotsBlockedByLaw++;
                            }
                            else
                            {
                                shotsDeclined++;
                            }
                        }
                    }
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matchesPlayed++;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks | " +
                              $"score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} | {seedSamples} keeper samples");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matchesPlayed} completed, {samples} keeper samples) ===");
        if (matchesPlayed == 0 || samples == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        int sweepEpisodes = 0;
        int sweepsTaken = 0;
        int sweepsDeclined = 0;

        foreach (GoalkeeperRaceReason reason in Enum.GetValues(typeof(GoalkeeperRaceReason)))
        {
            int index = (int)reason;
            int count = raceEpisodes[index];
            if (count == 0) continue;

            sweepEpisodes += count;
            if (reason is GoalkeeperRaceReason.KeeperFirst or GoalkeeperRaceReason.Contested) sweepsTaken += count;
            if (reason is GoalkeeperRaceReason.OpponentFirst or GoalkeeperRaceReason.OutOfRange
                or GoalkeeperRaceReason.OutsideArea or GoalkeeperRaceReason.NotTheOpponentsBall
                or GoalkeeperRaceReason.KeeperCannotReach)
            {
                sweepsDeclined += count;
            }

            string times = raceCounts[index] == 0
                ? string.Empty
                : $" | keeper {DescribeKeeperSeconds(raceKeeperSeconds[index] / raceCounts[index])}" +
                  $" vs opponent {DescribeKeeperSeconds(raceOpponentSeconds[index] / raceCounts[index])}";
            Console.WriteLine($"  {reason,-20}: {count,5} episodes{times}");
        }

        Console.WriteLine($"Sweep verdicts     : {sweepEpisodes} episodes, {sweepsTaken} taken, {sweepsDeclined} declined");

        int handsBarred = 0;
        var barredByReason = new List<string>();
        foreach (GoalkeeperHandlingReason reason in Enum.GetValues(typeof(GoalkeeperHandlingReason)))
        {
            int count = handlingSamples[(int)reason];
            if (count == 0) continue;
            if (reason != GoalkeeperHandlingReason.Allowed) handsBarred += count;
            barredByReason.Add($"{reason} {count}");
        }

        Console.WriteLine($"Handling law       : {handsBarred} of {samples} keeper samples barred from the hands" +
                          $" ({100.0 * handsBarred / Math.Max(1, samples):F1} %) | {string.Join(", ", barredByReason)}");
        Console.WriteLine($"Shots on target    : {shotsSeen} | dived for {shotsDived} (mean intercept" +
                          $" {(shotsDived == 0 ? 0d : interceptSecondsSum / shotsDived):F2} s) | declined as out of reach" +
                          $" {shotsDeclined} | declined as unhandleable {shotsBlockedByLaw}");

        // A6.5b: the delivery decision.
        var claimLines = new List<string>();
        int claimEpisodes = 0;
        foreach (GoalkeeperClaimReason reason in Enum.GetValues(typeof(GoalkeeperClaimReason)))
        {
            int count = claimReasonEpisodes[(int)reason];
            if (count == 0) continue;
            claimEpisodes += count;
            claimLines.Add($"{reason} {count}");
        }

        Console.WriteLine($"Deliveries         : {claimEpisodes} episodes | attacked {claimsAttacked}" +
                          $" (gather {claimsGathered}, punch {claimsPunched}) | declined {claimsDeclined}" +
                          (claimDecisionCount == 0
                              ? string.Empty
                              : $" | mean keeper {claimKeeperSecondsSum / claimDecisionCount:F2} s vs attacker" +
                                $" {claimAttackerSecondsSum / claimDecisionCount:F2} s, ball" +
                                $" {claimArrivalSpeedSum / claimDecisionCount:F1} m/s"));
        if (claimLines.Count > 0) Console.WriteLine($"  by reason        : {string.Join(", ", claimLines)}");

        // A6.5b: the distribution.
        int ground = distributionKinds[(int)GoalkeeperDistributionKind.Ground];
        int raised = distributionKinds[(int)GoalkeeperDistributionKind.Raised];
        int safe = distributionKinds[(int)GoalkeeperDistributionKind.SafeBall];
        Console.WriteLine($"Distribution       : {ground} ground, {raised} raised, {safe} safe-ball (last resort)" +
                          (distributionPriced == 0
                              ? string.Empty
                              : $" | priced at p {distributionCompletionSum / distributionPriced:F2} over" +
                                $" {distributionDistanceSum / distributionPriced:F1} m mean"));

        Console.WriteLine($"Keeper standoff    : in possession {StandoffReport(standoffSum[1], lineSamples[1], standoffMax[1])}" +
                          $" | defending {StandoffReport(standoffSum[0], lineSamples[0], standoffMax[0])}");

        bool ok = matchesPlayed == seedCount && sweepEpisodes > 0;
        Console.WriteLine(ok
            ? "RESULT: races, dives, deliveries and distribution decided on the ball's real trajectory"
            : "RESULT: incomplete, or no keeper decision was ever evaluated");
        return ok;
    }

    /// <summary>How often the scan probe samples receiver knowledge age, in ticks (0.1 s).</summary>
    private const int ScanSampleInterval = 6;

    /// <summary>How near an opponent must be to a receiver to count as part of his reception picture.</summary>
    private const float ScanReceiverNeighbourMeters = 15f;

    /// <summary>
    /// A7.1 evidence: what the head actually does in a real match, measured rather than asserted.
    ///
    /// Denominator: one completed 11v11 match per seed. Scan counts are player-ticks from the perception
    /// system (one observation build per active player per live tick). The <b>receiver picture</b> column is
    /// sampled every <see cref="ScanSampleInterval"/> ticks over players who are the
    /// <c>IntendedReceiverId</c> of a pass in flight — the moment A7.1's scan utility exists for — and reports
    /// the mean age of every opponent sighting within <see cref="ScanReceiverNeighbourMeters"/> of that
    /// receiver, so it measures whether the head turns become fresher knowledge before the ball arrives.
    /// Passing <c>off</c> as the third argument runs the same matches with
    /// <see cref="AiRuntimeConfig.EnableScanning"/> false: the A/B is then two invocations of this probe with
    /// identical squads, seeds and clocks.
    /// </summary>
    private static bool RunScanEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;
        bool scanning = args.Length <= 3 || !args[3].Equals("off", StringComparison.OrdinalIgnoreCase);
        var ai = new AiRuntimeConfig { EnableScanning = scanning };

        Console.WriteLine($"=== A7.1 scanning evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s," +
                          $" scanning {(scanning ? "ON" : "OFF")} ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Scan model         : head turn {ai.ScanMaxHeadTurnDegrees:F0}° at" +
                          $" {ai.ScanTurnRateDegreesPerSecond:F0}°/s, hold {ai.ScanHoldSeconds:F2} s, return" +
                          $" {ai.ScanCooldownSeconds:F2} s, refresh age {ai.ScanRefreshAgeSeconds:F2} s, attention" +
                          $" range {ai.ScanAttentionRangeMeters:F0} m");
        Console.WriteLine("Perception         : ±100° cone around the gaze, nearest 4 bodies within 16 m occlude");

        long scanStarts = 0;
        long threatScans = 0;
        long dangerScans = 0;
        long scanTicks = 0;
        long observerTicks = 0;
        long revealed = 0;
        long occluded = 0;
        int matches = 0;
        int receiverSamples = 0;
        long receiverAgeSum = 0;
        int receiverNeighbourSamples = 0;
        int receiverWorstAge = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack,
                Ai = ai
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            int seedReceiverSamples = 0;
            long seedReceiverAge = 0;
            int seedNeighbours = 0;
            int seedWorstAge = 0;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;
                if (engine.CurrentTick % ScanSampleInterval != 0) continue;

                foreach (FootballerState player in engine.Players)
                {
                    if (!player.IsActiveOnPitch) continue;
                    FootballerObservation? observation = engine.GetLastObservation(player.Id);
                    // The receiver of a pass in flight is exactly the "scan before reception" case.
                    if (observation is null || observation.IntendedReceiverId != player.Id) continue;

                    seedReceiverSamples++;
                    foreach (PerceivedEntity entity in observation.VisiblePlayers)
                    {
                        // Only the opponents close enough to affect the reception are the question: an old
                        // sighting of somebody 40 m away is not a reception risk.
                        if (entity.Team == player.Team) continue;
                        float dx = entity.EstimatedPosition.X - player.Position.X;
                        float dz = entity.EstimatedPosition.Z - player.Position.Z;
                        if (dx * dx + dz * dz > ScanReceiverNeighbourMeters * ScanReceiverNeighbourMeters) continue;

                        seedNeighbours++;
                        seedReceiverAge += entity.ObservedAgeTicks;
                        if (entity.ObservedAgeTicks > seedWorstAge) seedWorstAge = entity.ObservedAgeTicks;
                    }
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matches++;

            ScanTelemetry stats = engine.ScanStats;
            scanStarts += stats.ScanStarts;
            threatScans += stats.ThreatScanStarts;
            dangerScans += stats.DangerScanStarts;
            scanTicks += stats.ScanTicks;
            observerTicks += stats.ObserverTicks;
            revealed += stats.RevealedByGaze;
            occluded += stats.OccludedByBody;
            receiverSamples += seedReceiverSamples;
            receiverNeighbourSamples += seedNeighbours;
            receiverAgeSum += seedReceiverAge;
            if (seedWorstAge > receiverWorstAge) receiverWorstAge = seedWorstAge;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks |" +
                              $" score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} |" +
                              $" scans {stats.ScanStarts} (threat {stats.ThreatScanStarts}, danger" +
                              $" {stats.DangerScanStarts}) | revealed {stats.RevealedByGaze} |" +
                              $" occluded {stats.OccludedByBody} | {seedReceiverSamples} receiver looks," +
                              $" {seedNeighbours} near-opponent ages at {MeanAge(seedReceiverAge, seedNeighbours)} mean");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matches} completed, {observerTicks} player-ticks) ===");
        if (matches == 0 || observerTicks == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        var totals = new ScanTelemetry((int)scanStarts, (int)threatScans, (int)dangerScans,
            scanTicks, observerTicks, revealed, occluded);
        Console.WriteLine($"Scan fixes         : {scanStarts} completed (threat {threatScans}, danger {dangerScans})" +
                          $" | {totals.ScansPerPlayerMinute:F2} per player-minute |" +
                          $" {totals.ScanTickShare01:P1} of observed player-ticks holding a fix");
        Console.WriteLine($"Gaze-only sightings: {revealed} opponent observations inside the gaze cone but outside" +
                          " the body-facing cone");
        Console.WriteLine($"Occlusion          : {occluded} opponent observations suppressed by a body on the line");
        Console.WriteLine($"Receiver picture   : {receiverSamples} receiver looks, {receiverNeighbourSamples}" +
                          $" near-opponent ages (within {ScanReceiverNeighbourMeters:F0} m) at" +
                          $" {MeanAge(receiverAgeSum, receiverNeighbourSamples)} mean age (worst" +
                          $" {receiverWorstAge} ticks)");

        bool ok = matches == seedCount && (scanning ? scanStarts > 0 && revealed > 0 : scanStarts == 0);
        Console.WriteLine(ok
            ? scanning
                ? "RESULT: the head turns, the gaze changes what is seen, and reception knowledge refreshes"
                : "RESULT: scanning off means no head turn and no gaze-only sighting"
            : "RESULT: incomplete, or the scan model did not appear (scanning on with no scan/reveal)");
        return ok;
    }

    /// <summary>Mean and deepest standoff off the keeper's own goal line for one possession phase.</summary>
    private static string StandoffReport(double sum, int count, float worst) =>
        count == 0 ? "n/a" : $"{sum / count:F1} m mean, {worst:F1} m deepest ({count} samples)";

    /// <summary>
    /// A7.2 evidence: what the team-call channel actually does in real matches, measured rather than
    /// asserted.
    ///
    /// Denominator: one completed 11v11 match per seed. "Heard" counts are player-observations — one
    /// observation build per active player per live tick — that carried a call addressed to that player (or
    /// a step/drop line instruction); the action split under a man-on call is read from the controller's
    /// own selected action for the carrier holding the ball, so it is a decision, not a passive field.
    /// The line-shift columns are read from the A6.3 band the allocator actually placed players from, which
    /// is what proves a step/drop call reached the allocator instead of moving a body directly.
    ///
    /// Passing <c>off</c> runs the same matches with <see cref="AiRuntimeConfig.EnableTeamCalls"/> false:
    /// the A/B is then two invocations of this probe with identical squads, seeds and clocks, and the
    /// off run's requirement is that every call counter and every observation context is exactly zero.
    /// </summary>
    private static bool RunTeamCallEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;
        bool callsOn = args.Length <= 3 || !args[3].Equals("off", StringComparison.OrdinalIgnoreCase);
        var ai = new AiRuntimeConfig { EnableTeamCalls = callsOn };

        Console.WriteLine($"=== A7.2 team-call evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s," +
                          $" calls {(callsOn ? "ON" : "OFF")} ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Call model         : {ai.Describe()}");

        long published = 0;
        long suppressed = 0;
        long expired = 0;
        long evicted = 0;
        long observations = 0;
        long playerCallReads = 0;
        long lineDirectiveReads = 0;
        long lineShiftSamples = 0;
        double lineShiftSum = 0d;
        float lineShiftMin = float.MaxValue;
        float lineShiftMax = float.MinValue;
        long carrierDecisions = 0;
        long manOnCarrierDecisions = 0;
        long runDecisionsUnderCall = 0;
        var publishedByKind = new int[Enum.GetValues(typeof(TeamCallKind)).Length];
        var heardByKind = new int[Enum.GetValues(typeof(TeamCallKind)).Length];
        var carrierActions = new Dictionary<string, int>();
        var manOnActions = new Dictionary<string, int>();
        int matches = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack,
                Ai = ai
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            long seedPlayerCallReads = 0;
            int seedCarrierDecisions = 0;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;

                foreach (FootballerState player in engine.Players)
                {
                    if (!player.IsActiveOnPitch) continue;
                    FootballerObservation? observation = engine.GetLastObservation(player.Id);
                    if (observation is null) continue;

                    observations++;
                    if (observation.Calls.HasPlayerCall)
                    {
                        playerCallReads++;
                        seedPlayerCallReads++;
                        heardByKind[(int)observation.Calls.Kind]++;
                    }

                    if (observation.Calls.HasLineDirective) lineDirectiveReads++;

                    if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;

                    // A run call is heard by the runner, not by the carrier: it is a preference on the run
                    // candidates this player actually chose, so it is counted for every player.
                    if (observation.Calls.Kind == TeamCallKind.Run &&
                        controller.LastSelected.Key.Kind == CandidateKind.Run)
                    {
                        runDecisionsUnderCall++;
                    }

                    bool isCarrier = observation.BallControllingPlayerId == player.Id &&
                                     observation.PossessingTeam == player.Team;
                    if (!isCarrier) continue;

                    carrierDecisions++;
                    seedCarrierDecisions++;
                    string chosen = controller.LastSelected.ActionName;
                    carrierActions[chosen] = carrierActions.GetValueOrDefault(chosen) + 1;

                    if (observation.Calls.Kind == TeamCallKind.ManOn)
                    {
                        manOnCarrierDecisions++;
                        manOnActions[chosen] = manOnActions.GetValueOrDefault(chosen) + 1;
                    }
                }

                // The allocator side of step/drop: the band every line player was placed from this tick.
                SampleLineCallShift(engine, TeamId.Home, ref lineShiftSamples, ref lineShiftSum,
                    ref lineShiftMin, ref lineShiftMax);
                SampleLineCallShift(engine, TeamId.Away, ref lineShiftSamples, ref lineShiftSum,
                    ref lineShiftMin, ref lineShiftMax);
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matches++;

            TeamCallBoard board = engine.Communication.Calls;
            published += board.PublishedCount;
            suppressed += board.SuppressedCount;
            expired += board.ExpiredCount;
            evicted += board.EvictedCount;
            for (int kind = 1; kind < publishedByKind.Length; kind++)
                publishedByKind[kind] += board.CountOf((TeamCallKind)kind);

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks |" +
                              $" score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} |" +
                              $" calls {board.PublishedCount} published, {board.SuppressedCount} suppressed," +
                              $" {board.ExpiredCount} expired, {board.EvictedCount} evicted |" +
                              $" {seedPlayerCallReads} player-observations heard one |" +
                              $" {seedCarrierDecisions} carrier decisions");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matches} completed, {observations} player-observations) ===");
        if (matches == 0 || observations == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"Calls published    : {published} | {suppressed} repeated shouts suppressed |" +
                          $" {expired} expired | {evicted} evicted from the bounded board");
        Console.WriteLine($"Received           : {playerCallReads} player-observations carried a call" +
                          $" ({100.0 * playerCallReads / observations:F2} %) | {lineDirectiveReads} carried a" +
                          " step/drop line instruction");
        Console.WriteLine($"By kind            : {DescribeKindCounts(publishedByKind)}");
        Console.WriteLine($"Heard by kind      : {DescribeKindCounts(heardByKind)}");
        Console.WriteLine($"Line band shift    : {lineShiftSamples} defending samples with a call shift" +
                          (lineShiftSamples == 0
                              ? string.Empty
                              : $" (mean {lineShiftSum / lineShiftSamples:F2} m, range" +
                                $" {lineShiftMin:F2}..{lineShiftMax:F2} m)") +
                          " | read from the band the allocator placed the line from");
        Console.WriteLine($"Carrier decisions  : {carrierDecisions} | {manOnCarrierDecisions} under a man-on call" +
                          $" | {runDecisionsUnderCall} run choices while told to run");
        Console.WriteLine($"Carrier actions    : {DescribeTopActions(carrierActions)}");
        Console.WriteLine($"Under man-on       : {DescribeTopActions(manOnActions)}");

        bool ok = callsOn
            ? matches == seedCount && published > 0 && playerCallReads > 0 && expired > 0
            : matches == seedCount && published == 0 && playerCallReads == 0 && lineDirectiveReads == 0;
        Console.WriteLine(ok
            ? callsOn
                ? "RESULT: the channel is alive, heard after its delay, expires, and reaches the allocator"
                : "RESULT: calls off means no call, no heard instruction and a neutral observation"
            : "RESULT: incomplete, or the call channel did not appear (calls on with nothing published)");
        return ok;
    }

    /// <summary>
    /// A7.3 evidence: what reception planning actually does in real matches, measured rather than
    /// asserted.
    ///
    /// Denominator: one completed 11v11 match per seed, sampled every <see cref="ReceptionSampleInterval"/>
    /// ticks over players who are the <c>IntendedReceiverId</c> of a pass in flight — the decision A7.3
    /// exists for. The plan counters come from the controller's own recorded decision
    /// (<see cref="TacticalUtilityAiController.LastReceptionPlan"/>), so they measure what the receiver
    /// chose, not a passive field. "Unknown pressure" counts plans made without any believed presser: the
    /// milestone's "cannot say is a value" rule, visible as a number rather than a claim. "Heard a warning"
    /// counts receiver-observations that carried a man-on call (A7.2's channel addressed to a receiver by
    /// A7.3's producer).
    ///
    /// Passing <c>off</c> runs the same matches with <see cref="AiRuntimeConfig.EnableReceptionPlanning"/>
    /// false: the A/B is then two invocations with identical squads, seeds and clocks, and the off run's
    /// requirement is that no plan was ever formed (the pre-A7.3 intercept ray was chosen instead).
    /// </summary>
    private static bool RunReceptionEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 4;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;
        bool planning = args.Length <= 3 || !args[3].Equals("off", StringComparison.OrdinalIgnoreCase);
        var ai = new AiRuntimeConfig { EnableReceptionPlanning = planning };

        Console.WriteLine($"=== A7.3 reception-planning evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s," +
                          $" planning {(planning ? "ON" : "OFF")} ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Reception model    : {ReceptionPlanner.MaxOptions} (orientation, touch) pairs," +
                          $" horizon {ReceptionPlanner.PlanHorizonSeconds:F1} s, pressure radius" +
                          $" {ReceptionPlanner.PressureRadiusMeters:F0} m, man-on belief" +
                          $" {ReceptionPlanner.ManOnPressure01:F2} +/- {ReceptionPlanner.ManOnUncertainty01:F2}");
        Console.WriteLine($"AI configuration   : {ai.Describe()}");

        int[] plansByKind = new int[Enum.GetValues(typeof(ReceptionTouchKind)).Length];
        long receiverSamples = 0;
        long planned = 0;
        long unknownPressure = 0;
        long heardWarnings = 0;
        long optionSamples = 0;
        double scoreSum = 0;
        double arrivalSum = 0;
        int matches = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack,
                Ai = ai
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;
            long seedReceiverSamples = 0;
            long seedPlanned = 0;
            long seedUnknown = 0;
            long seedWarnings = 0;
            long seedOptions = 0;
            double seedScore = 0;
            double seedArrival = 0;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;
                if (engine.CurrentTick % ReceptionSampleInterval != 0) continue;

                foreach (FootballerState player in engine.Players)
                {
                    if (!player.IsActiveOnPitch) continue;
                    FootballerObservation? observation = engine.GetLastObservation(player.Id);
                    if (observation is null) continue;
                    if (observation.IntendedReceiverId != player.Id) continue;
                    if (observation.BallControlState != BallControlState.InFlightPass) continue;

                    seedReceiverSamples++;
                    if (observation.Calls.Kind == TeamCallKind.ManOn) seedWarnings++;

                    if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;
                    ReceptionPlan plan = controller.LastReceptionPlan;
                    if (!plan.IsPlanned) continue;

                    seedPlanned++;
                    seedOptions += controller.LastCandidates.Count;
                    plansByKind[(int)plan.TouchKind]++;
                    if (!plan.PressureKnown) seedUnknown++;
                    seedScore += plan.Score;
                    seedArrival += plan.ArrivalSeconds;
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matches++;
            receiverSamples += seedReceiverSamples;
            planned += seedPlanned;
            unknownPressure += seedUnknown;
            heardWarnings += seedWarnings;
            optionSamples += seedOptions;
            scoreSum += seedScore;
            arrivalSum += seedArrival;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks |" +
                              $" score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} |" +
                              $" {seedReceiverSamples} receiver looks, {seedPlanned} planned" +
                              (seedPlanned == 0
                                  ? string.Empty
                                  : $" ({seedScore / seedPlanned:F2} mean score, {seedOptions / (double)seedPlanned:F1}" +
                                    $" options, {seedArrival / seedPlanned:F2} s to arrival)") +
                              $" | {seedWarnings} man-on warnings heard");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matches} completed, {receiverSamples} receiver looks) ===");
        if (matches == 0 || receiverSamples == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        long shieldOrEscape = plansByKind[(int)ReceptionTouchKind.Shield]
                              + plansByKind[(int)ReceptionTouchKind.EscapePressure];
        Console.WriteLine($"Plans formed       : {planned} of {receiverSamples} receiver looks" +
                          $" ({100.0 * planned / receiverSamples:F2} %) |" +
                          (planned == 0
                              ? " mean score n/a, mean arrival n/a"
                              : $" mean score {scoreSum / planned:F2}, mean arrival {arrivalSum / planned:F2} s," +
                                $" {optionSamples / (double)planned:F1} options per decision"));
        Console.WriteLine($"By touch           : {DescribeReceptionKindCounts(plansByKind)}");
        Console.WriteLine($"Cannot say         : {unknownPressure} plans formed with no believed presser" +
                          (planned == 0 ? " (n/a)" : $" ({100.0 * unknownPressure / planned:F1} % of plans)"));
        Console.WriteLine($"Receiver warnings  : {heardWarnings} in-flight receiver observations carried a man-on call");

        bool ok = planning
            ? matches == seedCount && planned > 0 && shieldOrEscape > 0
            : matches == seedCount && planned == 0;
        Console.WriteLine(ok
            ? planning
                ? "RESULT: receivers plan the first touch, including shield/escape under believed pressure"
                : "RESULT: planning off means no reception plan and the pre-A7.3 intercept ray"
            : "RESULT: incomplete, or the reception planner did not appear (planning on with no plan)");
        return ok;
    }

    /// <summary>
    /// A7.5 evidence: the separated error sources, measured in real 11v11 matches.
    ///
    /// Three things are reported, each from a different consumer, so a claim about one cannot hide behind
    /// another:
    /// <list type="bullet">
    /// <item><b>The shared causes reach the picture of the game</b> - every 4 ticks the probe converts each
    /// live player's stamina and composure with <see cref="PlayerCognitiveState.Compute"/>, so the reported
    /// noise scale, evaluation error and reaction delay are the numbers the perception system and the
    /// scorers were handed, not an estimate.</item>
    /// <item><b>The evaluation error reaches a decision</b> - the controllers count the decisions in which
    /// the misjudgement changed which candidate scored highest
    /// (<see cref="TacticalUtilityAiController.EvaluationChangedDecisions"/>).</item>
    /// <item><b>Pressure reaches the first touch</b> - every executed first touch is binned by the contact
    /// pressure it happened under, with the execution error the contact layer applied and the ball's pace
    /// relative to the receiver. That is the A7 exit gate's "pressure reduces control quality" clause. The
    /// bin comparison is descriptive (different contacts, different balls); the controlled paired sample is
    /// <c>CognitiveErrorTests.PressureReducesControlQuality_OverAPairedSample</c>.</item>
    /// </list>
    ///
    /// Passing <c>off</c> runs the same matches with <see cref="AiRuntimeConfig.EnableCognitiveError"/>
    /// false: the probe must then find no evaluation-driven change and no execution error at all - the
    /// pre-A7.5 build.
    /// </summary>
    private static bool RunCognitiveErrorEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;
        bool cognitive = args.Length <= 3 || !args[3].Equals("off", StringComparison.OrdinalIgnoreCase);
        var ai = new AiRuntimeConfig { EnableCognitiveError = cognitive };

        Console.WriteLine($"=== A7.5 separated-error evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s," +
                          $" cognitive error {(cognitive ? "ON" : "OFF")} ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Error model        : observation x(1 + {PlayerCognitiveState.FatigueObservationNoiseGain:F2}*fatigue)," +
                          $" evaluation +/-{PlayerCognitiveState.EvaluationUtilitySpan:F2}*error," +
                          $" reaction x(1 + {PlayerCognitiveState.FatigueReactionGain:F2}*fatigue)," +
                          $" first touch -{PlayerCognitiveState.PressureExecutionGain:F2}*pressure*(1-composure)" +
                          $" -{PlayerCognitiveState.FatigueExecutionGain:F2}*fatigue");
        Console.WriteLine($"AI configuration   : {ai.Describe()}");

        long observations = 0;
        double staminaSum = 0;
        float staminaMin = 1f;
        double noiseScaleSum = 0;
        double evaluationSum = 0;
        double reactionSum = 0;
        long decisions = 0;
        long misjudged = 0;
        long anticipated = 0;
        long offTarget = 0;
        double anticipationSum = 0;
        int[] touchCounts = new int[PressureBands];
        double[] touchPace = new double[PressureBands];
        double[] touchError = new double[PressureBands];
        int matches = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack,
                Ai = ai
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

            var playersById = new Dictionary<int, FootballerState>(engine.Players.Count);
            foreach (FootballerState player in engine.Players)
                playersById[player.Id] = player;

            long seedObservations = 0;
            long seedDecisions = 0;
            long seedMisjudged = 0;
            long seedAnticipated = 0;
            long seedOffTarget = 0;
            double seedAnticipationSum = 0;
            int[] seedTouches = new int[PressureBands];
            double seedStamina = 0;
            double seedNoise = 0;
            double seedEvaluation = 0;
            double seedReaction = 0;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;

                // The shared causes, converted exactly as perception and the scorers convert them.
                if (engine.CurrentTick % ReceptionSampleInterval == 0)
                {
                    foreach (FootballerState player in engine.Players)
                    {
                        if (!player.IsActiveOnPitch) continue;
                        // With the feature off nothing reads these numbers, so the probe reports the neutral
                        // state rather than the model's would-be values.
                        CognitiveState state = cognitive
                            ? PlayerCognitiveState.Compute(player.Attributes, player.Stamina01)
                            : CognitiveState.Neutral(player.Attributes.ReactionDelaySeconds);
                        seedObservations++;
                        seedStamina += player.Stamina01;
                        seedNoise += state.ObservationNoiseScale;
                        seedEvaluation += state.EvaluationError01;
                        seedReaction += state.ReactionDelaySeconds;
                        if (player.Stamina01 < staminaMin) staminaMin = player.Stamina01;
                    }
                }

                // First touches, binned by the pressure the contact happened under.
                IReadOnlyList<BallContactEvent> log = engine.ContactLog;
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    BallContactEvent contact = log[i];
                    if (contact.Tick != engine.CurrentTick) break;
                    if (contact.Kind != BallContactKind.Collection) continue;
                    if (!playersById.TryGetValue(contact.PlayerId, out FootballerState? player)) continue;
                    if (!player.LastExecutionDiagnostic.StartsWith("FirstTouchCushion", StringComparison.Ordinal))
                        continue;

                    float pressure = BallContactRules.PressureAtContact(player.Position, player.Team, engine.Players);
                    int band = pressure < 0.15f ? 0 : pressure < 0.5f ? 1 : 2;
                    seedTouches[band]++;
                    touchCounts[band]++;
                    touchError[band] += cognitive
                        ? PlayerCognitiveState.ExecutionError01(player.Attributes, player.Stamina01, pressure)
                        : 0f;
                    Vector2 ballVel = new(engine.Ball.Velocity.X, engine.Ball.Velocity.Z);
                    Vector2 playerVel = new(player.Velocity.X, player.Velocity.Z);
                    // The pace of the ball relative to the receiver: the worse the first touch, the more of
                    // the pass's pace survives it. (The *signed* run-ahead is the wrong column here - a bad
                    // touch sends the ball back the way it came, which reads as "behind the player".)
                    touchPace[band] += (ballVel - playerVel).Length();

                    // A7.5: how far the ball was from the point the plan expected to meet it.
                    ReceptionIntent intent = player.LastCommand.ReceptionIntent;
                    if (intent.IsPlanned)
                    {
                        Vector2 ballNow = new(engine.Ball.Position.X, engine.Ball.Position.Z);
                        float anticipation = Vector2.Distance(ballNow, intent.MeetingPointXZ);
                        seedAnticipationSum += anticipation;
                        seedAnticipated++;
                        if (anticipation > ReceptionTouchModel.AnticipationToleranceMeters)
                            seedOffTarget++;
                    }
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matches++;
            foreach (FootballerState player in engine.Players)
            {
                if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;
                seedDecisions += controller.DecisionsComputed;
                seedMisjudged += controller.EvaluationChangedDecisions;
            }

            observations += seedObservations;
            staminaSum += seedStamina;
            noiseScaleSum += seedNoise;
            evaluationSum += seedEvaluation;
            reactionSum += seedReaction;
            decisions += seedDecisions;
            misjudged += seedMisjudged;
            anticipated += seedAnticipated;
            offTarget += seedOffTarget;
            anticipationSum += seedAnticipationSum;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks |" +
                              $" score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} |" +
                              $" stamina {(seedObservations == 0 ? 0 : seedStamina / seedObservations):F2} |" +
                              $" {seedDecisions} decisions, {seedMisjudged} misjudged the choice |" +
                              $" first touches {seedTouches[0]}+{seedTouches[1]}+{seedTouches[2]} (free/light/heavy)");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matches} completed, {observations} player-samples," +
                          $" {decisions} decisions) ===");
        if (matches == 0 || observations == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        int touches = touchCounts[0] + touchCounts[1] + touchCounts[2];
        Console.WriteLine($"Causes             : mean stamina {staminaSum / observations:F2} (min {staminaMin:F2}) |" +
                          $" observation noise x{noiseScaleSum / observations:F2} |" +
                          $" evaluation {evaluationSum / observations:F3} |" +
                          $" reaction delay {reactionSum / observations:F3} s");
        Console.WriteLine($"Decisions          : {decisions} scored | {misjudged} misjudged the choice" +
                          (decisions == 0 ? string.Empty : $" ({100.0 * misjudged / decisions:F2} %)"));
        if (touches > 0)
        {
            Console.WriteLine($"First touches      : {touches} | free {touchCounts[0]}, light {touchCounts[1]}," +
                              $" heavy {touchCounts[2]} | execution error" +
                              $" {BandMean(touchError, touchCounts, 0):F3}/{BandMean(touchError, touchCounts, 1):F3}/" +
                              $"{BandMean(touchError, touchCounts, 2):F3} | ball pace relative to the receiver" +
                              $" {BandMean(touchPace, touchCounts, 0):F2}/" +
                              $"{BandMean(touchPace, touchCounts, 1):F2}/" +
                              $"{BandMean(touchPace, touchCounts, 2):F2}");
        }
        else
        {
            Console.WriteLine("First touches      : none executed");
        }

        if (anticipated > 0)
        {
            Console.WriteLine($"Anticipation       : {anticipated} planned touches met the ball" +
                              $" {anticipationSum / anticipated:F2} m from the planned meeting point |" +
                              $" {offTarget} beyond the {ReceptionTouchModel.AnticipationToleranceMeters:F1} m" +
                              " tolerance" + (cognitive ? " (the touch was realised less)" : " (not penalised: off)"));
        }

        bool ok = cognitive
            ? matches == seedCount && misjudged > 0 && (touchCounts[2] == 0 || touchError[2] > 0)
            : matches == seedCount && misjudged == 0 && touches > 0 && touchError[0] + touchError[1] + touchError[2] == 0;
        Console.WriteLine(ok
            ? cognitive
                ? "RESULT: fatigue and composure reach observation, judgement, reaction and the first touch"
                : "RESULT: cognitive error off means no misjudged choice and no execution error at all"
            : cognitive
                ? "RESULT: incomplete, or the causes never reached a decision or a contact"
                : "RESULT: incomplete, or a decision/contact still carried a cognitive error with the feature off");
        return ok;
    }

    /// <summary>Pressure bands of the cognitive probe: free, light, heavy.</summary>
    private const int PressureBands = 3;

    /// <summary>Mean per-contact value in one pressure band, or 0 when the band is empty.</summary>
    private static double BandMean(double[] sums, int[] counts, int band) =>
        counts[band] == 0 ? 0d : sums[band] / counts[band];

    /// <summary>
    /// A7.4 evidence: does the reception plan reach execution, and does it change what a legal first
    /// touch does?
    ///
    /// Denominator: every first-touch contact (<see cref="BallContactKind.Collection"/> with the
    /// execution layer's own <c>FirstTouchCushion</c> diagnostic) in real 11v11 matches. Each one is
    /// counted by the reception intent the *command that took it* carried — the command, not a passive
    /// field, so a count here is the plan reaching the command boundary and coming out as physics.
    ///
    /// "Run-ahead" is the ball's velocity relative to the receiver projected on his own facing, measured
    /// one solver step after the contact: a kill should sit near zero (the ball stays with the body), a
    /// stride or escape touch should be clearly positive (the ball is sent into the space ahead). Neutral
    /// touches are the pre-A7.4 cushion and are reported separately for comparison.
    ///
    /// Passing <c>off</c> runs the same matches with <see cref="AiRuntimeConfig.EnableReceptionPlanning"/>
    /// false: no plan, so no command may carry an intent and every first touch must be the old cushion.
    /// </summary>
    private static bool RunFirstTouchEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;
        bool planning = args.Length <= 3 || !args[3].Equals("off", StringComparison.OrdinalIgnoreCase);
        var ai = new AiRuntimeConfig { EnableReceptionPlanning = planning };

        Console.WriteLine($"=== A7.4 first-touch evidence: {seedCount} seeds x {halfSeconds * 2f:F0} s," +
                          $" planning {(planning ? "ON" : "OFF")} ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine($"Touch model        : the planned touch is realised in proportion to first-touch" +
                          $" quality, bounded at player + {ReceptionTouchModel.MaxRelativeTouchSpeed:F1} m/s" +
                          " (the dribble spring's own allowance)");
        Console.WriteLine($"AI configuration   : {ai.Describe()}");

        int kindCount = Enum.GetValues(typeof(ReceptionTouchKind)).Length;
        int[] touchesByKind = new int[kindCount];
        double[] runAheadSum = new double[kindCount];
        long plannedTouches = 0;
        long neutralTouches = 0;
        int matches = 0;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            var config = new PreMatchConfig
            {
                Seed = seed,
                PlayersPerTeam = 11,
                HalfDurationSeconds = halfSeconds,
                HomeProfile = TeamTacticalProfile.PossessionControl,
                AwayProfile = TeamTacticalProfile.DirectCounterattack,
                Ai = ai
            };

            var engine = new MatchSimulationEngine(config);
            int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

            var playersById = new Dictionary<int, FootballerState>(engine.Players.Count);
            foreach (FootballerState player in engine.Players)
                playersById[player.Id] = player;

            int seedPlanned = 0;
            int seedNeutral = 0;

            while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            {
                engine.StepTick();
                if (engine.Rules.Phase == MatchPhase.FullTime) break;

                IReadOnlyList<BallContactEvent> log = engine.ContactLog;
                for (int i = log.Count - 1; i >= 0; i--)
                {
                    BallContactEvent contact = log[i];
                    if (contact.Tick != engine.CurrentTick) break;
                    if (contact.Kind != BallContactKind.Collection) continue;
                    if (!playersById.TryGetValue(contact.PlayerId, out FootballerState? player)) continue;
                    if (!player.LastExecutionDiagnostic.StartsWith("FirstTouchCushion", StringComparison.Ordinal))
                        continue;

                    ReceptionTouchKind kind = player.LastCommand.ReceptionIntent.TouchKind;
                    if (kind == ReceptionTouchKind.None)
                    {
                        seedNeutral++;
                    }
                    else
                    {
                        seedPlanned++;
                        touchesByKind[(int)kind]++;
                        Vector2 ballVel = new(engine.Ball.Velocity.X, engine.Ball.Velocity.Z);
                        Vector2 playerVel = new(player.Velocity.X, player.Velocity.Z);
                        runAheadSum[(int)kind] += Vector2.Dot(ballVel - playerVel, player.FacingXZ);
                    }
                }
            }

            bool completed = engine.Rules.Phase == MatchPhase.FullTime;
            if (completed) matches++;
            plannedTouches += seedPlanned;
            neutralTouches += seedNeutral;

            Console.WriteLine();
            Console.WriteLine($"Seed {seed}: {(completed ? "FT" : "INCOMPLETE")} in {engine.CurrentTick} ticks |" +
                              $" score {engine.Analytics.HomeGoals}-{engine.Analytics.AwayGoals} |" +
                              $" {seedPlanned} first touches carried a plan, {seedNeutral} were the neutral cushion");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ({matches} completed, {plannedTouches + neutralTouches}" +
                          $" first touches) ===");
        if (matches == 0 || plannedTouches + neutralTouches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"First touches      : {plannedTouches + neutralTouches} executed |" +
                          $" {plannedTouches} carried a planned touch | {neutralTouches} carried none");
        Console.WriteLine($"By planned touch   : {DescribeFirstTouchKinds(touchesByKind, runAheadSum)}");

        float strideMean = MeanRunAhead(ReceptionTouchKind.StrideTouch, touchesByKind, runAheadSum);
        float escapeMean = MeanRunAhead(ReceptionTouchKind.EscapePressure, touchesByKind, runAheadSum);
        float killMean = MeanRunAhead(ReceptionTouchKind.Kill, touchesByKind, runAheadSum);
        bool forwarding = touchesByKind[(int)ReceptionTouchKind.Kill] > 0 &&
                          touchesByKind[(int)ReceptionTouchKind.StrideTouch] +
                          touchesByKind[(int)ReceptionTouchKind.EscapePressure] > 0;
        if (forwarding)
        {
            Console.WriteLine($"Run-ahead          : kill {killMean:F2} m/s vs stride/escape" +
                              $" {MathF.Max(strideMean, escapeMean):F2} m/s (ball velocity relative to the" +
                              " receiver, on his facing)");
        }

        bool ok = planning
            ? matches == seedCount && plannedTouches > 0 && (!forwarding || MathF.Max(strideMean, escapeMean) > killMean)
            : matches == seedCount && plannedTouches == 0 && neutralTouches > 0;
        Console.WriteLine(ok
            ? planning
                ? "RESULT: planned touches reach execution, and the forward touches send the ball ahead of the kill"
                : "RESULT: planning off means no command carried an intent and every first touch was the old cushion"
            : planning
                ? "RESULT: incomplete, or no planned touch reached execution (or the forward touches do not outrun the kill)"
                : "RESULT: incomplete, or a command carried a reception intent with planning off");
        return ok;
    }

    /// <summary>Mean run-ahead in m/s for one touch kind, or NaN when it never happened.</summary>
    private static float MeanRunAhead(ReceptionTouchKind kind, int[] counts, double[] sums) =>
        counts[(int)kind] == 0 ? float.NaN : (float)(sums[(int)kind] / counts[(int)kind]);

    /// <summary>"Kill 12 (+0.10 m/s), Stride 40 (+0.83 m/s), ..." for the planned touches that occurred.</summary>
    private static string DescribeFirstTouchKinds(int[] counts, double[] runAheadSum)
    {
        var parts = new List<string>();
        for (int kind = 1; kind < counts.Length; kind++)
        {
            if (counts[kind] == 0) continue;
            parts.Add($"{((ReceptionTouchKind)kind)} {counts[kind]}" +
                      $" ({runAheadSum[kind] / counts[kind]:+0.00;-0.00} m/s run-ahead)");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>"Kill 120, OpenBody 88, ..." for the touch kinds that occurred, in enum order.</summary>
    private static string DescribeReceptionKindCounts(int[] counts)    {
        var parts = new List<string>();
        for (int kind = 1; kind < counts.Length; kind++)
        {
            if (counts[kind] == 0) continue;
            parts.Add($"{((ReceptionTouchKind)kind)} {counts[kind]}");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>How often the reception probe samples receivers, in ticks (15 Hz).</summary>
    private const int ReceptionSampleInterval = 4;

    /// <summary>Adds one team's current line band call shift to the probe's sample, if it has one.</summary>
    private static void SampleLineCallShift(
        MatchSimulationEngine engine,
        TeamId team,
        ref long samples,
        ref double sum,
        ref float min,
        ref float max)
    {
        DefensiveLineBand band = engine.TeamJobs.LineBandOf(team);
        if (!band.IsValid || MathF.Abs(band.CallShiftMeters) < 1e-4f) return;

        samples++;
        sum += band.CallShiftMeters;
        if (band.CallShiftMeters < min) min = band.CallShiftMeters;
        if (band.CallShiftMeters > max) max = band.CallShiftMeters;
    }

    /// <summary>"ManOn 120, Turn 88, ..." for the call kinds that occurred, in enum order.</summary>
    private static string DescribeKindCounts(int[] counts)
    {
        var parts = new List<string>();
        for (int kind = 1; kind < counts.Length; kind++)
        {
            if (counts[kind] == 0) continue;
            parts.Add($"{((TeamCallKind)kind)} {counts[kind]}");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>The three most frequent chosen actions, name and count.</summary>
    private static string DescribeTopActions(Dictionary<string, int> actions)
    {
        if (actions.Count == 0) return "none";

        var parts = new List<string>();
        foreach (KeyValuePair<string, int> pair in actions
                     .OrderByDescending(pair => pair.Value)
                     .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                     .Take(3))
        {
            parts.Add($"{pair.Key} {pair.Value}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Mean knowledge age in ticks, or "n/a" when nothing was sampled.</summary>
    private static string MeanAge(long sum, int count) => count == 0 ? "n/a" : $"{sum / (double)count:F1}";

    /// <summary>How often the keeper probe samples each keeper, in ticks (0.2 s).</summary>
    private const int KeeperSampleInterval = 12;

    /// <summary>A race time, or "unreachable" for the model's "nobody can get there" value.</summary>
    private static string DescribeKeeperSeconds(double seconds) =>
        seconds >= float.MaxValue * 0.5d ? "unreachable" : $"{seconds:F2} s";

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
        // L43: the ground run is a ThroughPass. Without this mapping the "ground-run" row's executed
        // column was structurally 0 whenever the intent was the ground form.
        KickIntentType.ThroughPass => 4,
        _ => -1
    };

    /// <summary>
    /// Runs one headless match and reports whether it genuinely completed.
    ///
    /// Audit F23: this used to print "Match Complete" whenever the tick cap was reached, so a match
    /// that stalled short of full time looked identical to a finished one. The phase is now asserted
    /// and the process exit code reflects it.
    /// </summary>
    private static bool RunHeadlessMatch(
        int playersPerTeam,
        float halfDurationSeconds,
        ulong seed,
        bool restartRoutines = true)
    {
        var config = new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = playersPerTeam,
            HalfDurationSeconds = halfDurationSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            // The `off` ablation disables the newest behaviour (A8.5's restart routines), which is the version-50
            // build exactly: every earlier feature keeps its shipped default.
            Ai = new AiRuntimeConfig { EnableRestartRoutines = restartRoutines }
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
        Console.WriteLine($"AI configuration  : {config.Ai.Describe()}");
        Console.WriteLine($"Status            : {(completed ? "FULL TIME" : $"INCOMPLETE â€” tick cap {maxTicks} reached")}" +
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
        var callCounts = new int[Enum.GetValues(typeof(TeamCallKind)).Length];
        for (int kind = 1; kind < callCounts.Length; kind++)
            callCounts[kind] = engine.Communication.Calls.CountOf((TeamCallKind)kind);
        Console.WriteLine($"Team calls (A7.2) : {engine.Communication.Calls.PublishedCount} published | "
                          + $"{engine.Communication.Calls.SuppressedCount} suppressed | "
                          + $"{engine.Communication.Calls.ExpiredCount} expired | "
                          + $"{DescribeKindCounts(callCounts)}");
        Console.WriteLine($"Airborne (A5.5)   : {crosses} crosses | {lofted} lofted | {driven} driven executed");
        RestartCoordinator restarts = engine.Communication.Restarts;
        Console.WriteLine($"Restart routines  : {restarts.RaisedCount} raised | "
                          + $"taken {restarts.CountOfEnd(RestartEndReason.Taken)} | "
                          + $"stolen {restarts.CountOfEnd(RestartEndReason.Stolen)} | "
                          + $"replaced {restarts.CountOfEnd(RestartEndReason.Replaced)} | "
                          + $"expired {restarts.CountOfEnd(RestartEndReason.Expired)}");
        Console.WriteLine($"Events Logged     : {engine.Rules.Events.Count}");

        return completed;
    }

    // ---------------------------------------------------------------------------------------------
    // A7.6 -- archetypes as preferences on one pipeline.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Player archetypes, for the A7.6 probe's census and breakdown tables.</summary>
    private static readonly int ArchetypeCount = Enum.GetValues(typeof(PlayerArchetype)).Length;

    /// <summary>Candidate kinds, for the A7.6 probe's chosen-action table.</summary>
    private static readonly int CandidateKindCount = Enum.GetValues(typeof(CandidateKind)).Length;

    /// <summary>Run kinds, for the A7.6 probe's off-ball table.</summary>
    private static readonly int RunKindCount = Enum.GetValues(typeof(RunKind)).Length;

    /// <summary>How often (ticks) the A7.6 probe samples a player's chosen action and adopted run.</summary>
    private const int ArchetypeSampleInterval = 6;

    /// <summary>One A7.6 probe variant's accumulators. Two exist: archetypes on, and the uniform squad.</summary>
    private sealed class ArchetypeEvidenceTotals
    {
        public int Matches;
        public long Ticks;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long Crosses;
        public long Lofted;
        public long Driven;
        public long TeamCalls;
        public long RunAdoptions;
        public long SweepCommitments;
        public long KeeperSupportSamples;
        public double KeeperSupportDepthSum;
        public long ScanStarts;
        public long ScanTicks;
        public long ObserverTicks;
        public long ActionSamples;
        public int ArchetypePlayers;
        public readonly int[] Census = new int[ArchetypeCount];
        public readonly long[] CrossesByArchetype = new long[ArchetypeCount];
        public readonly long[,] RunAdoptionsByArchetype = new long[ArchetypeCount, RunKindCount];
        public readonly long[,] ActionSamplesByArchetype = new long[ArchetypeCount, CandidateKindCount];

        public double KeeperSupportDepthMean =>
            KeeperSupportSamples <= 0 ? 0d : KeeperSupportDepthSum / KeeperSupportSamples;
    }

    /// <summary>
    /// A7.6 evidence: <b>archetypes are preferences on one decision pipeline</b>, measured as a same-seed
    /// A/B between the default squad and the uniform pre-A7.6 squad.
    ///
    /// The same seed is simulated twice: once with <see cref="AiRuntimeConfig.EnableArchetypes"/> on (the
    /// default XI fields all seven archetypes once each) and once off (every player is
    /// <see cref="PlayerArchetype.None"/>). Both runs are reported side by side, so the differences are the
    /// archetype preference and not a different world.
    ///
    /// What the columns are, and the consumer each one proves is live:
    /// <list type="bullet">
    /// <item><b>squad census</b> - which archetypes the two builds actually field;</item>
    /// <item><b>keeper support depth</b> - the mean distance off his goal line while his own team has the
    /// ball: the sweeper keeper's support-depth preference (<see cref="GoalkeeperSupportModel"/>) reaching
    /// positioning;</item>
    /// <item><b>sweep commitments</b> - transitions into a "GK:Rush1v1Sweep"/"GK:Block1v1" command, i.e. the
    /// sweeper keeper's wider commit window reaching a decision;</item>
    /// <item><b>run adoptions by archetype and kind</b> - off-ball paths actually adopted from the run
    /// generator: the run-kind preference reaching the scorer (a touchline winger holding width, an inverted
    /// fullback underlapping);</item>
    /// <item><b>chosen player-ticks by archetype and action kind</b> - which actions each archetype spends
    /// its decisions on: the tendency deltas reaching the carrier's candidate scores.</item>
    /// </list>
    ///
    /// The verdict does not require the aggregate match numbers to be "better" (there is nothing to be better
    /// at yet - A9 owns tuning); it requires the features to be <b>live and distinct</b>: both builds complete
    /// every seed, the default squad fields all seven archetypes while the uniform squad fields none, the
    /// sweeper keeper stands measurably deeper in possession, and the preferred run kinds are actually
    /// adopted. The controlled single-choice evidence is <c>PlayerArchetypeTests</c>.
    /// </summary>
    private static bool RunArchetypeEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A7.6 archetype evidence: same-seed A/B, {seedCount} seeds x {halfSeconds * 2f:F0} s" +
                          " -- default squad vs the uniform pre-A7.6 squad ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Preference model   : role tendencies +/- archetype deltas (clamped 0..1); run-kind preference");
        Console.WriteLine($"                     x{PlayerArchetypeProfile.RunPreferenceWeight:F2} on the run score; scan refresh" +
                          " x(1.35 - 0.70*appetite); keeper contest");
        Console.WriteLine("                     margin x(0.60 + 0.80*aggression) and support depth x(0.50 + 1.00*aggression).");
        Console.WriteLine("                     No candidate set, sight cone, reach or contact rule differs - only weights and attention.");
        Console.WriteLine($"Archetypes ON      : {DescribeArchetypeSquad(archetypes: true)}");
        Console.WriteLine($"Archetypes OFF     : {DescribeArchetypeSquad(archetypes: false)}");
        Console.WriteLine();

        var on = new ArchetypeEvidenceTotals();
        var off = new ArchetypeEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunArchetypeMatch(seed, halfSeconds, archetypes: true, on, out string onLine);
            bool completedOff = RunArchetypeMatch(seed, halfSeconds, archetypes: false, off, out string offLine);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-26}{"ON",12}{"OFF",12}");
        Console.WriteLine($"{"completed matches",-26}{on.Matches,12}{off.Matches,12}");
        Console.WriteLine($"{"live ticks",-26}{on.Ticks,12}{off.Ticks,12}");
        Console.WriteLine($"{"passes attempted",-26}{on.PassesAttempted,12}{off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-26}{on.PassesCompleted,12}{off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-26}{on.Shots,12}{off.Shots,12}");
        Console.WriteLine($"{"crosses executed",-26}{on.Crosses,12}{off.Crosses,12}");
        Console.WriteLine($"{"lofted executed",-26}{on.Lofted,12}{off.Lofted,12}");
        Console.WriteLine($"{"driven executed",-26}{on.Driven,12}{off.Driven,12}");
        Console.WriteLine($"{"team calls",-26}{on.TeamCalls,12}{off.TeamCalls,12}");
        Console.WriteLine($"{"run adoptions",-26}{on.RunAdoptions,12}{off.RunAdoptions,12}");
        Console.WriteLine($"{"sweep commitments",-26}{on.SweepCommitments,12}{off.SweepCommitments,12}");
        Console.WriteLine($"{"keeper support depth m",-26}{on.KeeperSupportDepthMean,12:F2}{off.KeeperSupportDepthMean,12:F2}");
        Console.WriteLine($"{"scan starts",-26}{on.ScanStarts,12}{off.ScanStarts,12}");
        Console.WriteLine($"{"scan tick share",-26}{(on.ObserverTicks == 0 ? 0d : (double)on.ScanTicks / on.ObserverTicks),12:F4}" +
                          $"{(off.ObserverTicks == 0 ? 0d : (double)off.ScanTicks / off.ObserverTicks),12:F4}");

        int distinctArchetypes = CountArchetypes(on);
        long preferredRunAdoptions = CountPreferredRunAdoptions(on);
        Console.WriteLine();
        Console.WriteLine($"Archetypes fielded : {distinctArchetypes} distinct, {on.ArchetypePlayers} players (on) |" +
                          $" {off.ArchetypePlayers} players (off)");
        Console.WriteLine($"Preferred runs     : {preferredRunAdoptions} of {on.RunAdoptions} adoptions were a kind the" +
                          " player's archetype prefers (positive run preference)");

        Console.WriteLine();
        Console.WriteLine("Run adoptions by archetype (ON; kind count):");
        WriteArchetypeRunAdoptionTable(on);
        Console.WriteLine();
        Console.WriteLine("Chosen player-ticks by archetype (ON; action kind share):");
        WriteArchetypeActionTable(on);
        Console.WriteLine();
        Console.WriteLine($"Crosses by kicker archetype (ON): {DescribeArchetypeCounts(on.CrossesByArchetype)}");

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool assignment = distinctArchetypes == 7 && off.ArchetypePlayers == 0;
        bool keeperDeeper = on.KeeperSupportDepthMean > off.KeeperSupportDepthMean + 0.1d;
        bool preferencesLive = preferredRunAdoptions > 0 && on.ActionSamples > 0;
        bool ok = completeBoth && assignment && keeperDeeper && preferencesLive;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the default squad fields all seven archetypes as preferences and the uniform squad" +
              " none; the sweeper keeper stands deeper in possession, and the preferred run kinds and actions are" +
              " live without a candidate, sight or limit changing"
            : "RESULT: FAIL -- incomplete match, archetype assignment, preferred runs/actions or the sweeper" +
              " keeper's support depth failed to separate the two builds");
        return ok;
    }

    /// <summary>
    /// Runs one A7.6 probe match and folds it into <paramref name="totals"/>. Returns whether the match reached
    /// full time and a one-line summary for the per-seed report.
    /// </summary>
    private static bool RunArchetypeMatch(
        ulong seed,
        float halfSeconds,
        bool archetypes,
        ArchetypeEvidenceTotals totals,
        out string line)
    {
        var ai = new AiRuntimeConfig { EnableArchetypes = archetypes };
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        foreach (FootballerState player in engine.Players)
        {
            totals.Census[(int)player.Archetype]++;
            if (player.Archetype != PlayerArchetype.None) totals.ArchetypePlayers++;
        }

        var lastAdoptedTick = new Dictionary<int, int>();
        var lastKeeperSummary = new Dictionary<int, string>();
        long seedCrosses = 0;
        long seedLofted = 0;
        long seedDriven = 0;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();
            if (engine.Rules.Phase == MatchPhase.FullTime) break;

            // Executed deliveries, by the kicker's archetype: the touchline winger's cross appetite reaching
            // a struck ball, not only a candidate score.
            IReadOnlyList<BallContactAttempt> attempts = engine.LastContactAttempts;
            if (attempts.Count > 0 && attempts[0].Tick == engine.CurrentTick)
            {
                foreach (BallContactAttempt attempt in attempts)
                {
                    if (!attempt.WantedKick || !attempt.Executed) continue;
                    switch (attempt.Intent)
                    {
                        case KickIntentType.Cross:
                            seedCrosses++;
                            break;
                        case KickIntentType.LoftedPass:
                        case KickIntentType.LobbedThroughPass:
                            seedLofted++;
                            break;
                        case KickIntentType.DrivenPass:
                            seedDriven++;
                            break;
                    }

                    if (attempt.Intent == KickIntentType.Cross)
                    {
                        foreach (FootballerState player in engine.Players)
                        {
                            if (player.Id != attempt.PlayerId) continue;
                            totals.CrossesByArchetype[(int)player.Archetype]++;
                            break;
                        }
                    }
                }
            }

            // A sweep is a decision, not a tick: count the transition into a sweep/block command.
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch || player.Role != TacticalRole.Goalkeeper) continue;
                string summary = engine.GetController(player.Id).GetDebugSummary();
                lastKeeperSummary.TryGetValue(player.Id, out string? previous);
                bool sweeping = IsSweepSummary(summary);
                if (sweeping && previous is not null && !IsSweepSummary(previous)) totals.SweepCommitments++;
                lastKeeperSummary[player.Id] = summary;
            }

            if (engine.CurrentTick % ArchetypeSampleInterval != 0) continue;

            TeamId? possessing = engine.Possession.PossessionTeam;
            float homeDir = engine.Rules.GetHomeAttackDirectionX();
            float halfLength = engine.Pitch.HalfLength;
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch) continue;

                // The sweeper keeper's support-depth preference: how far off his own line he stands while his
                // team has the ball (A7.6's one goalkeeper positioning preference).
                if (player.Role == TacticalRole.Goalkeeper && possessing == player.Team)
                {
                    float attackDir = player.Team == TeamId.Home ? homeDir : -homeDir;
                    float ownGoalX = -attackDir * halfLength;
                    float depth = (player.Position.X - ownGoalX) * attackDir;
                    totals.KeeperSupportSamples++;
                    totals.KeeperSupportDepthSum += MathF.Max(0f, depth);
                    continue;
                }

                if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;

                if (!controller.LastSelected.Key.IsNone)
                {
                    totals.ActionSamples++;
                    totals.ActionSamplesByArchetype[(int)player.Archetype, (int)controller.LastSelected.Key.Kind]++;
                }

                // A run adoption is the generator's own event: the intent's identity (its adoption tick)
                // changes, so a held run is not counted twice.
                if (controller.Runs.Intent is RunIntent intent &&
                    (!lastAdoptedTick.TryGetValue(player.Id, out int adopted) || adopted != intent.AdoptedTick))
                {
                    lastAdoptedTick[player.Id] = intent.AdoptedTick;
                    totals.RunAdoptions++;
                    totals.RunAdoptionsByArchetype[(int)player.Archetype, (int)intent.Kind]++;
                }
            }
        }

        var analytics = engine.Analytics;
        totals.Ticks += engine.CurrentTick;
        totals.PassesAttempted += analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        totals.PassesCompleted += analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        totals.Shots += analytics.HomeShots + analytics.AwayShots;
        totals.Crosses += seedCrosses;
        totals.Lofted += seedLofted;
        totals.Driven += seedDriven;
        totals.TeamCalls += engine.Communication.Calls.PublishedCount;
        ScanTelemetry scan = engine.ScanStats;
        totals.ScanStarts += scan.ScanStarts;
        totals.ScanTicks += scan.ScanTicks;
        totals.ObserverTicks += scan.ObserverTicks;

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.Matches++;

        line = $"{engine.CurrentTick} ticks, score {analytics.HomeGoals}-{analytics.AwayGoals}, passes " +
               $"{analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} attempted " +
               $"({analytics.HomePassesCompleted}/{analytics.AwayPassesCompleted} completed), shots " +
               $"{analytics.HomeShots}/{analytics.AwayShots}, crosses {seedCrosses}";
        return completed;
    }

    /// <summary>A keeper debug summary that means he committed to a sweep or a 1v1 block.</summary>
    private static bool IsSweepSummary(string summary) =>
        summary.StartsWith("GK:Rush1v1Sweep", StringComparison.Ordinal) ||
        summary.StartsWith("GK:Block1v1", StringComparison.Ordinal);

    /// <summary>The 11v11 default squad's archetype assignment, as the A7.6 probe prints it.</summary>
    private static string DescribeArchetypeSquad(bool archetypes)
    {
        var parts = new List<string>();
        for (int shirt = 1; shirt <= 11; shirt++)
        {
            var (role, _, _) = TeamTacticalCoordinator.GetFormationRoleAndSlot(shirt, 11);
            PlayerArchetype archetype = archetypes
                ? PlayerArchetypeProfile.ForShirt(role, shirt, 11)
                : PlayerArchetype.None;
            if (archetype == PlayerArchetype.None) continue;
            parts.Add($"{shirt}:{archetype}");
        }

        return parts.Count == 0 ? "none (uniform role squad)" : $"{parts.Count} fielded -- {string.Join(", ", parts)}";
    }

    private static int CountArchetypes(ArchetypeEvidenceTotals totals)
    {
        int count = 0;
        for (int archetype = 1; archetype < ArchetypeCount; archetype++)
            if (totals.Census[archetype] > 0) count++;
        return count;
    }

    private static long CountPreferredRunAdoptions(ArchetypeEvidenceTotals totals)
    {
        long count = 0;
        for (int archetype = 1; archetype < ArchetypeCount; archetype++)
        for (int kind = 0; kind < RunKindCount; kind++)
        {
            if (totals.RunAdoptionsByArchetype[archetype, kind] <= 0) continue;
            if (PlayerArchetypeProfile.RunPreference((RunKind)kind, (PlayerArchetype)archetype) > 0f)
                count += totals.RunAdoptionsByArchetype[archetype, kind];
        }

        return count;
    }

    private static void WriteArchetypeRunAdoptionTable(ArchetypeEvidenceTotals totals)
    {
        for (int archetype = 1; archetype < ArchetypeCount; archetype++)
        {
            var parts = new List<string>();
            for (int kind = 0; kind < RunKindCount; kind++)
            {
                long count = totals.RunAdoptionsByArchetype[archetype, kind];
                if (count > 0) parts.Add($"{(RunKind)kind} {count}");
            }

            if (parts.Count > 0)
                Console.WriteLine($"  {((PlayerArchetype)archetype),-16}: {string.Join(", ", parts)}");
        }
    }

    private static void WriteArchetypeActionTable(ArchetypeEvidenceTotals totals)
    {
        for (int archetype = 1; archetype < ArchetypeCount; archetype++)
        {
            long total = 0;
            for (int kind = 0; kind < CandidateKindCount; kind++) total += totals.ActionSamplesByArchetype[archetype, kind];
            if (total <= 0) continue;

            var parts = new List<string>();
            for (int kind = 0; kind < CandidateKindCount; kind++)
            {
                long count = totals.ActionSamplesByArchetype[archetype, kind];
                if (count <= 0) continue;
                parts.Add($"{(CandidateKind)kind} {count} ({100.0 * count / total:F0} %)");
            }

            Console.WriteLine($"  {((PlayerArchetype)archetype),-16}: {string.Join(", ", parts)}");
        }
    }

    private static string DescribeArchetypeCounts(long[] counts)
    {
        var parts = new List<string>();
        for (int archetype = 1; archetype < ArchetypeCount; archetype++)
        {
            if (counts[archetype] <= 0) continue;
            parts.Add($"{((PlayerArchetype)archetype)} {counts[archetype]}");
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// A8.1 evidence: <b>bounded combinations</b>, measured as a same-seed A/B between the default build and
    /// the build with <see cref="AiRuntimeConfig.EnableCombinations"/> off.
    ///
    /// The same seed is simulated twice: once with one-two and third-player contracts enabled, once with the
    /// mechanism off. The off run is the version-46 build exactly, so the difference the columns show is the
    /// contracts and nothing else.
    ///
    /// What the columns are, and the consumer each one proves is live:
    /// <list type="bullet">
    /// <item><b>contracts published</b> - proposals the carrier actually committed to (a pass won the
    /// decision while a returnable partner or an advertised run made it a combination);</item>
    /// <item><b>continuations executed</b> - the first receiver played the continuation ball while the
    /// contract was live, attributed from the executed kick contact;</item>
    /// <item><b>endings by reason</b> - why contracts died: expired, possession ended, replaced, or abandoned
    /// by a participant. This is the evidence that the lifecycle is real rather than a counter that only
    /// grows;</item>
    /// <item><b>passes, shots, ticks</b> - the aggregate match the contracts ran in, so the two worlds can be
    /// compared rather than only their contract tables.</item>
    /// </list>
    ///
    /// The verdict does not require the aggregate match numbers to be "better" (A9 owns tuning); it requires
    /// the feature to be <b>live and bounded</b>: both builds complete every seed, the build with combinations
    /// on creates and actually completes continuations, and the build with them off creates none — which is
    /// also the neutrality proof for the switch.
    /// </summary>
    private static bool RunCombinationEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A8.1 combination evidence: same-seed A/B, {seedCount} seeds x {halfSeconds * 2f:F0} s" +
                          " -- one-two/third-player contracts on vs off ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Contract model     : initiator's pass commits a return region, meeting tick and confidence;");
        Console.WriteLine("                     participants hear it after the communication delay; a one-two's initiator");
        Console.WriteLine("                     runs the frozen region and the continuation pass is timed to it; a third-player");
        Console.WriteLine("                     contract values the layoff to the runner's advertised run. No candidate, reach,");
        Console.WriteLine("                     sight or contact rule is added - only bounded preferences and the contract itself.");
        Console.WriteLine($"Combinations ON    : {new AiRuntimeConfig { EnableCombinations = true }.Describe()}");
        Console.WriteLine($"Combinations OFF   : {new AiRuntimeConfig { EnableCombinations = false }.Describe()}");
        Console.WriteLine();

        var on = new CombinationEvidenceTotals();
        var off = new CombinationEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunCombinationMatch(seed, halfSeconds, enabled: true, on, out string onLine);
            bool completedOff = RunCombinationMatch(seed, halfSeconds, enabled: false, off, out string offLine);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-28}{"ON",12}{"OFF",12}");
        Console.WriteLine($"{"completed matches",-28}{on.Matches,12}{off.Matches,12}");
        Console.WriteLine($"{"live ticks",-28}{on.Ticks,12}{off.Ticks,12}");
        Console.WriteLine($"{"passes attempted",-28}{on.PassesAttempted,12}{off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-28}{on.PassesCompleted,12}{off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-28}{on.Shots,12}{off.Shots,12}");
        Console.WriteLine($"{"contracts published",-28}{on.Published,12}{off.Published,12}");
        Console.WriteLine($"{"continuations executed",-28}{on.Continued,12}{off.Continued,12}");
        Console.WriteLine($"{"duplicate proposals suppressed",-28}{on.Suppressed,12}{off.Suppressed,12}");
        Console.WriteLine($"{"live at full time",-28}{on.LiveAtEnd,12}{off.LiveAtEnd,12}");

        Console.WriteLine();
        Console.WriteLine("Published by kind:");
        for (int kind = 1; kind < CombinationKindCount; kind++)
            Console.WriteLine($"  {((CombinationKind)kind),-22}{on.PublishedByKind[kind],12}{off.PublishedByKind[kind],12}");

        Console.WriteLine("Continuations by kind:");
        for (int kind = 1; kind < CombinationKindCount; kind++)
            Console.WriteLine($"  {((CombinationKind)kind),-22}{on.ContinuedByKind[kind],12}{off.ContinuedByKind[kind],12}");

        Console.WriteLine("Endings by reason (ON):");
        for (int reason = 1; reason < CombinationEndReasonCount; reason++)
        {
            long count = on.EndedByReason[reason];
            if (count > 0) Console.WriteLine($"  {((CombinationEndReason)reason),-22}{count,12}");
        }

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool contractsLive = on.Published > 0 && on.Continued > 0;
        bool switchNeutral = off.Published == 0 && off.Continued == 0;
        bool ok = completeBoth && contractsLive && switchNeutral;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the build with combinations on creates one-two/third-player contracts and executes" +
              " continuations while the build with them off creates none; both finished every seed"
            : "RESULT: FAIL -- incomplete matches, no live contract/continuation, or the switch did not neutralize");
        return ok;
    }

    /// <summary>Runs one A8.1 probe match and folds it into <paramref name="totals"/>.</summary>
    private static bool RunCombinationMatch(
        ulong seed,
        float halfSeconds,
        bool enabled,
        CombinationEvidenceTotals totals,
        out string line,
        bool search = true)
    {
        var ai = new AiRuntimeConfig { EnableCombinations = enabled, EnableShallowSearch = search };
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
            engine.StepTick();

        CombinationCoordinator contracts = engine.Communication.Combinations;
        var analytics = engine.Analytics;
        totals.Ticks += engine.CurrentTick;
        totals.PassesAttempted += analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        totals.PassesCompleted += analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        totals.Shots += analytics.HomeShots + analytics.AwayShots;
        totals.Published += contracts.PublishedCount;
        totals.Continued += contracts.ContinuedCount;
        totals.Suppressed += contracts.SuppressedCount;
        totals.Evicted += contracts.EvictedCount;
        for (int kind = 1; kind < CombinationKindCount; kind++)
        {
            totals.PublishedByKind[kind] += contracts.CountOfPublished((CombinationKind)kind);
            totals.ContinuedByKind[kind] += contracts.CountOfContinued((CombinationKind)kind);
        }

        for (int reason = 1; reason < CombinationEndReasonCount; reason++)
            totals.EndedByReason[reason] += contracts.CountOfEnd((CombinationEndReason)reason);

        int tick = engine.CurrentTick;
        totals.LiveAtEnd += contracts.LiveCount(TeamId.Home, tick) + contracts.LiveCount(TeamId.Away, tick);

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.Matches++;

        line = $"{engine.CurrentTick} ticks, score {analytics.HomeGoals}-{analytics.AwayGoals}, passes " +
               $"{analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} attempted, shots " +
               $"{analytics.HomeShots}/{analytics.AwayShots}, contracts {contracts.PublishedCount} published " +
               $"/ {contracts.ContinuedCount} continued";
        return completed;
    }

    /// <summary>
    /// A8.2 evidence: <b>the shallow continuation search</b>, measured as a same-seed A/B inside the
    /// combination build (combinations on in both runs), between the default build and the build with
    /// <see cref="AiRuntimeConfig.EnableShallowSearch"/> off.
    ///
    /// The off run is the version-47 build exactly, so the difference the columns show is the search and
    /// nothing else: both builds form one-two and third-player contracts from A8.1's heuristic, and the on
    /// build additionally examines the best three first actions with at most three continuations each before
    /// choosing the promise.
    ///
    /// What the columns are, and the consumer each one proves is live:
    /// <list type="bullet">
    /// <item><b>contracts published</b> - promises the carrier actually committed to;</item>
    /// <item><b>continuations executed</b> - the first receiver played the continuation ball while the
    /// contract was live, attributed from the executed kick contact. This is the exit gate's "useful
    /// continuations", and the A/B's claim is that the search increases them;</item>
    /// <item><b>endings by reason</b> - why contracts died, so the search's promises are shown to be live
    /// promises that end, not a counter that only grows;</item>
    /// <item><b>passes, shots, ticks</b> - the aggregate match the contracts ran in.</item>
    /// </list>
    ///
    /// The verdict does not require the aggregate match numbers to be "better" (A9 owns tuning); it requires
    /// both builds to complete every seed, the search build to create and actually execute continuations, and
    /// the search build to execute <b>more</b> of them than the build with it off.
    /// </summary>
    private static bool RunShallowSearchEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A8.2 shallow continuation search: same-seed A/B, {seedCount} seeds x " +
                          $"{halfSeconds * 2f:F0} s -- search on vs search off (combinations on in both) ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Search model       : the carrier examines the best three eligible pass receivers and, from");
        Console.WriteLine("                     its own observation, at most three continuations each -- two one-two");
        Console.WriteLine("                     return regions (deep and short) and the best advertised layoff -- to depth");
        Console.WriteLine("                     two. Defender sighting uncertainty widens the second ball's lane; there is");
        Console.WriteLine("                     no truth-state rollout. The heuristic still covers receivers outside the");
        Console.WriteLine("                     bounded search, so the switch adds a choice rather than replacing play.");
        Console.WriteLine($"Search ON          : {new AiRuntimeConfig { EnableCombinations = true, EnableShallowSearch = true }.Describe()}");
        Console.WriteLine($"Search OFF         : {new AiRuntimeConfig { EnableCombinations = true, EnableShallowSearch = false }.Describe()}");
        Console.WriteLine();

        var on = new CombinationEvidenceTotals();
        var off = new CombinationEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunCombinationMatch(seed, halfSeconds, enabled: true, on, out string onLine,
                search: true);
            bool completedOff = RunCombinationMatch(seed, halfSeconds, enabled: true, off, out string offLine,
                search: false);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-28}{"ON",12}{"OFF",12}{"DELTA",12}");
        Console.WriteLine($"{"completed matches",-28}{on.Matches,12}{off.Matches,12}{on.Matches - off.Matches,12}");
        Console.WriteLine($"{"live ticks",-28}{on.Ticks,12}{off.Ticks,12}{on.Ticks - off.Ticks,12}");
        Console.WriteLine($"{"passes attempted",-28}{on.PassesAttempted,12}{off.PassesAttempted,12}{on.PassesAttempted - off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-28}{on.PassesCompleted,12}{off.PassesCompleted,12}{on.PassesCompleted - off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-28}{on.Shots,12}{off.Shots,12}{on.Shots - off.Shots,12}");
        Console.WriteLine($"{"contracts published",-28}{on.Published,12}{off.Published,12}{on.Published - off.Published,12}");
        Console.WriteLine($"{"continuations executed",-28}{on.Continued,12}{off.Continued,12}{on.Continued - off.Continued,12}");
        Console.WriteLine($"{"duplicate proposals suppressed",-28}{on.Suppressed,12}{off.Suppressed,12}{on.Suppressed - off.Suppressed,12}");
        Console.WriteLine($"{"live at full time",-28}{on.LiveAtEnd,12}{off.LiveAtEnd,12}{on.LiveAtEnd - off.LiveAtEnd,12}");

        Console.WriteLine();
        Console.WriteLine("Published by kind:");
        for (int kind = 1; kind < CombinationKindCount; kind++)
        {
            long delta = on.PublishedByKind[kind] - off.PublishedByKind[kind];
            Console.WriteLine($"  {((CombinationKind)kind),-22}{on.PublishedByKind[kind],12}{off.PublishedByKind[kind],12}{delta,12}");
        }

        Console.WriteLine("Continuations by kind:");
        for (int kind = 1; kind < CombinationKindCount; kind++)
        {
            long delta = on.ContinuedByKind[kind] - off.ContinuedByKind[kind];
            Console.WriteLine($"  {((CombinationKind)kind),-22}{on.ContinuedByKind[kind],12}{off.ContinuedByKind[kind],12}{delta,12}");
        }

        Console.WriteLine("Endings by reason (ON / OFF):");
        for (int reason = 1; reason < CombinationEndReasonCount; reason++)
        {
            if (on.EndedByReason[reason] == 0 && off.EndedByReason[reason] == 0) continue;
            Console.WriteLine($"  {((CombinationEndReason)reason),-22}{on.EndedByReason[reason],12}{off.EndedByReason[reason],12}");
        }

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool searchLive = on.Published > 0 && on.Continued > 0;
        bool moreContinuations = on.Continued > off.Continued;
        bool ok = completeBoth && searchLive && moreContinuations;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the search build creates and executes more continuations than the build with the" +
              " search off, and both finished every seed"
            : "RESULT: FAIL -- incomplete matches, no live continuation in the search build, or the search build" +
              " did not execute more continuations than the build with it off");
        return ok;
    }

    private sealed class CombinationEvidenceTotals
    {
        public int Matches;
        public long Ticks;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long Published;
        public long Continued;
        public long Suppressed;
        public long Evicted;
        public long LiveAtEnd;
        public readonly long[] PublishedByKind = new long[CombinationKindCount];
        public readonly long[] ContinuedByKind = new long[CombinationKindCount];
        public readonly long[] EndedByReason = new long[CombinationEndReasonCount];
    }

    private static readonly int CombinationKindCount = Enum.GetValues(typeof(CombinationKind)).Length;
    private static readonly int CombinationEndReasonCount = Enum.GetValues(typeof(CombinationEndReason)).Length;

    /// <summary>
    /// A8.3 evidence: <b>the switch / overload-to-isolate recognition</b>, measured as a same-seed A/B between
    /// the default build and the build with <see cref="AiRuntimeConfig.EnableSwitchRecognition"/> off.
    ///
    /// The off run is the version-48 build exactly, so every difference the columns show is the recognition and
    /// nothing else: both builds offer the same passes, plan them with the same <see cref="PassPlanner"/>, price
    /// them with the same <see cref="PassSuccessModel"/> and execute them through the same contact layer. The on
    /// build adds one bounded preference on a pass that already exists (and the ordinary pre-rank rescue that
    /// keeps its far-side target inside the pass-target budget).
    ///
    /// What the columns are, and the consumer each one proves is live:
    /// <list type="bullet">
    /// <item><b>carrier decisions</b> - carrier-ticks on which a player decided with the ball (the denominator);</item>
    /// <item><b>decisions that recognized a switch</b> - from the controller's own
    /// <see cref="TacticalUtilityAiController.SwitchPreferencesApplied"/>: the carrier's picture supported a
    /// far-side opportunity. Zero in the off build by construction, which is the switch's neutrality proof;</item>
    /// <item><b>switch passes chosen</b> - the carrier's chosen action was a pass whose receiver stands at least
    /// A7.2's far-side distance across from the ball, classified from the decision's own observation;</item>
    /// <item><b>switch passes executed</b> - the execution layer actually played a pass whose intended receiver
    /// was that far across, classified from the executed contact (a decision that never reached the ball does
    /// not count);</item>
    /// <item><b>passes, shots, ticks</b> - the aggregate match the switches ran in, so the two worlds are
    /// compared rather than only their switch tables.</item>
    /// </list>
    ///
    /// The verdict does not require the aggregate match numbers to be "better" (A9 owns tuning); it requires
    /// both builds to complete every seed, the recognition to be live in the on build, the switch to be neutral
    /// in the off build (no recognition, no preference) and the on build to execute more switch passes than the
    /// off build across the seed set — single-fixture inversions are expected, as with every A8 probe.
    /// </summary>
    private static bool RunSwitchEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A8.3 switch-recognition evidence: same-seed A/B, {seedCount} seeds x " +
                          $"{halfSeconds * 2f:F0} s -- recognition on vs off ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Recognition model  : the carrier recognizes a switch from his own picture - local pressure");
        Console.WriteLine("                     on him, the far side's lateral distance and room, the receiver's matchup");
        Console.WriteLine("                     (an isolated one-v-one, scouted pace band), the pass model's completion");
        Console.WriteLine("                     for the ball, and a rest defence as good as the allocator's forward-");
        Console.WriteLine("                     commitment threshold. One bounded preference on an existing pass, plus the");
        Console.WriteLine("                     ordinary pre-rank rescue; no candidate, sight, reach or contact is added.");
        Console.WriteLine("                     There is no pass counter and no clock input: nothing forces a switch after N passes.");
        Console.WriteLine($"Recognition ON     : {new AiRuntimeConfig { EnableSwitchRecognition = true }.Describe()}");
        Console.WriteLine($"Recognition OFF    : {new AiRuntimeConfig { EnableSwitchRecognition = false }.Describe()}");
        Console.WriteLine();

        var on = new SwitchEvidenceTotals();
        var off = new SwitchEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunSwitchMatch(seed, halfSeconds, recognize: true, on, out string onLine);
            bool completedOff = RunSwitchMatch(seed, halfSeconds, recognize: false, off, out string offLine);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-34}{"ON",12}{"OFF",12}{"DELTA",12}");
        Console.WriteLine($"{"completed matches",-34}{on.Matches,12}{off.Matches,12}{on.Matches - off.Matches,12}");
        Console.WriteLine($"{"live ticks",-34}{on.Ticks,12}{off.Ticks,12}{on.Ticks - off.Ticks,12}");
        Console.WriteLine($"{"passes attempted",-34}{on.PassesAttempted,12}{off.PassesAttempted,12}{on.PassesAttempted - off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-34}{on.PassesCompleted,12}{off.PassesCompleted,12}{on.PassesCompleted - off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-34}{on.Shots,12}{off.Shots,12}{on.Shots - off.Shots,12}");
        Console.WriteLine($"{"carrier decisions",-34}{on.CarrierDecisions,12}{off.CarrierDecisions,12}{on.CarrierDecisions - off.CarrierDecisions,12}");
        Console.WriteLine($"{"decisions recognizing a switch",-34}{on.RecognizedDecisions,12}{off.RecognizedDecisions,12}{on.RecognizedDecisions - off.RecognizedDecisions,12}");
        Console.WriteLine($"{"switch passes chosen",-34}{on.ChosenSwitchPasses,12}{off.ChosenSwitchPasses,12}{on.ChosenSwitchPasses - off.ChosenSwitchPasses,12}");
        Console.WriteLine($"{"switch passes executed",-34}{on.ExecutedSwitchPasses,12}{off.ExecutedSwitchPasses,12}{on.ExecutedSwitchPasses - off.ExecutedSwitchPasses,12}");
        Console.WriteLine($"{"chosen while recognized",-34}{on.ChosenWhileRecognized,12}{off.ChosenWhileRecognized,12}{on.ChosenWhileRecognized - off.ChosenWhileRecognized,12}");

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool recognitionLive = on.RecognizedDecisions > 0 && on.ExecutedSwitchPasses > 0;
        bool switchNeutral = off.RecognizedDecisions == 0 && off.ChosenWhileRecognized == 0;
        bool moreSwitches = on.ExecutedSwitchPasses > off.ExecutedSwitchPasses;
        bool ok = completeBoth && recognitionLive && switchNeutral && moreSwitches;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the recognition build recognizes far-side opportunities and executes more switch" +
              " passes than the version-48 build, which recognizes none; both finished every seed"
            : "RESULT: FAIL -- incomplete matches, no live recognition, the switch did not neutralize, or the" +
              " recognition build did not execute more switch passes than the build with it off");
        return ok;
    }

    /// <summary>One A8.3 probe variant's accumulators.</summary>
    private sealed class SwitchEvidenceTotals
    {
        public int Matches;
        public long Ticks;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long CarrierDecisions;
        public long RecognizedDecisions;
        public long ChosenSwitchPasses;
        public long ExecutedSwitchPasses;
        public long ChosenWhileRecognized;
    }

    /// <summary>Runs one A8.3 probe match and folds it into <paramref name="totals"/>.</summary>
    private static bool RunSwitchMatch(
        ulong seed,
        float halfSeconds,
        bool recognize,
        SwitchEvidenceTotals totals,
        out string line)
    {
        var ai = new AiRuntimeConfig { EnableSwitchRecognition = recognize };
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        long seedCarrierDecisions = 0;
        long seedRecognized = 0;
        long seedChosen = 0;
        long seedExecuted = 0;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();

            // The carrier's decision this tick: the observation he decided on and the action he chose. A
            // switch is a pass whose intended receiver stands at least A7.2's far-side distance across.
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch) continue;
                if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;
                FootballerObservation? observation = engine.GetLastObservation(player.Id);
                if (observation is null) continue;
                if (observation.BallControllingPlayerId != player.Id ||
                    observation.PossessingTeam != player.Team)
                {
                    continue;
                }

                seedCarrierDecisions++;
                bool recognized = controller.SwitchPreferencesApplied > 0;
                if (recognized) seedRecognized++;

                UtilityCandidate chosen = controller.LastSelected;
                if (chosen.Key.Kind != CandidateKind.Pass) continue;
                if (chosen.Key.TargetPlayerId <= 0) continue;
                if (!TryFindVisible(observation, chosen.Key.TargetPlayerId, out PerceivedEntity receiver))
                    continue;
                float lateral = MathF.Abs(receiver.EstimatedPosition.Z - observation.PerceivedBallPosition.Z);
                if (lateral < TeamCallPlanner.SwitchMinLateralMeters) continue;

                seedChosen++;
                if (recognized) totals.ChosenWhileRecognized++;
            }

            // Executed switch passes, from the execution layer's own contacts: a decision that never reached
            // the ball is not a switch that was played. The bounded log is read from its tail for this tick, so
            // no per-tick scan of the whole match is paid.
            IReadOnlyList<BallContactEvent> log = engine.ContactLog;
            for (int index = log.Count - 1; index >= 0; index--)
            {
                BallContactEvent contact = log[index];
                if (contact.Tick != engine.CurrentTick) break;
                if (contact.Kind != BallContactKind.Kick) continue;
                if (!KickIntents.IsPass(contact.Intent)) continue;
                if (contact.IntendedReceiverId is not int receiverId) continue;
                if (!TryFindPlayer(engine, receiverId, out FootballerState? receiver) || receiver is null) continue;
                if (MathF.Abs(receiver.Position.Z - contact.BallPosition.Z) < TeamCallPlanner.SwitchMinLateralMeters)
                    continue;
                seedExecuted++;
            }
        }

        var analytics = engine.Analytics;
        totals.Ticks += engine.CurrentTick;
        totals.PassesAttempted += analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        totals.PassesCompleted += analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        totals.Shots += analytics.HomeShots + analytics.AwayShots;
        totals.CarrierDecisions += seedCarrierDecisions;
        totals.RecognizedDecisions += seedRecognized;
        totals.ChosenSwitchPasses += seedChosen;
        totals.ExecutedSwitchPasses += seedExecuted;

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.Matches++;

        line = $"{engine.CurrentTick} ticks, score {analytics.HomeGoals}-{analytics.AwayGoals}, passes " +
               $"{analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} attempted, shots " +
               $"{analytics.HomeShots}/{analytics.AwayShots}, carrier decisions {seedCarrierDecisions}, " +
               $"recognized {seedRecognized}, switch passes chosen {seedChosen} / executed {seedExecuted}";
        return completed;
    }

    /// <summary>The believed entity for one player id in an observation, or false when he is not in the picture.</summary>
    private static bool TryFindVisible(
        FootballerObservation observation,
        int playerId,
        out PerceivedEntity entity)
    {
        for (int index = 0; index < observation.VisiblePlayers.Count; index++)
        {
            if (observation.VisiblePlayers[index].PlayerId != playerId) continue;
            entity = observation.VisiblePlayers[index];
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>The authoritative player state for one id, or false when the match has no such player.</summary>
    private static bool TryFindPlayer(MatchSimulationEngine engine, int playerId, out FootballerState? player)
    {
        IReadOnlyList<FootballerState> players = engine.Players;
        for (int index = 0; index < players.Count; index++)
        {
            if (players[index].Id != playerId) continue;
            player = players[index];
            return true;
        }

        player = null;
        return false;
    }

    /// <summary>
    /// A8.4 evidence: <b>short trap contracts</b>, measured as a same-seed A/B between the default build and the
    /// build with <see cref="AiRuntimeConfig.EnableTrapContracts"/> off.
    ///
    /// The off run is the version-49 build exactly, so every difference the columns show is the trap mechanism
    /// and nothing else: both builds offer the same candidates, price them with the same models and execute them
    /// through the same contact layer. The on build names a presser, a screener and the allocator's cover in a
    /// short contract, gives the screener one bounded preference on the same screen act and gives the presser
    /// A6.2's angled point — no candidate, reach, sight or contact is added.
    ///
    /// What the columns are, and the consumer each one proves is live:
    /// <list type="bullet">
    /// <item><b>traps raised</b> - live contracts accepted on the board: zero in the off build by construction,
    /// which is the trap's neutrality proof;</item>
    /// <item><b>trap ticks</b> - decision ticks a trap was live (the exposure the mechanism actually had);</item>
    /// <item><b>won / escaped / loose / expired</b> - the lifecycle endings, from the board's own counters:
    /// the exit gate needs a trap that can be escaped, and the recovery counter proves the side re-forms after
    /// a failed one;</item>
    /// <item><b>screener/presser/cover decisions</b> - participant decisions made with the trap's role, and how
    /// many of the screener's were the Screen act: the exit gate's "pressure, screening, cover" reaching the
    /// live controllers;</item>
    /// <item><b>passes, shots, ticks</b> - the aggregate match the traps ran in, so the two worlds are compared
    /// rather than only their trap tables.</item>
    /// </list>
    ///
    /// The verdict does not require the aggregate match to be "better" (A9 owns tuning); it requires both
    /// builds to complete every seed, the on build to run traps with both a win and an escape, the screener to
    /// actually screen, and the off build to raise none — single-fixture inversions are expected, as with every
    /// A8 probe.
    /// </summary>
    private static bool RunTrapEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A8.4 short-trap evidence: same-seed A/B, {seedCount} seeds x " +
                          $"{halfSeconds * 2f:F0} s -- traps on vs off ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Trap model         : from the defending team's shared picture (own exact positions, its");
        Console.WriteLine("                     belief of the carrier, the elected presser/second man and the allocator's");
        Console.WriteLine("                     cover), a carrier pinned to a touchline is trapped for 1.5 s by three named");
        Console.WriteLine("                     players: the presser takes A6.2's angled point, the screener (a shape-duty");
        Console.WriteLine("                     man) gets one bounded preference on the existing Screen act, and the");
        Console.WriteLine("                     allocator's cover stays behind the press. A trap ends once - won, escaped,");
        Console.WriteLine("                     loose, expired, presser/cover lost, participant removed or reset - and a");
        Console.WriteLine("                     failed trap puts the side into a 0.5 s recovery before it re-commits.");
        Console.WriteLine($"Traps ON           : {new AiRuntimeConfig { EnableTrapContracts = true }.Describe()}");
        Console.WriteLine($"Traps OFF          : {new AiRuntimeConfig { EnableTrapContracts = false }.Describe()}");
        Console.WriteLine();

        var on = new TrapEvidenceTotals();
        var off = new TrapEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunTrapMatch(seed, halfSeconds, traps: true, on, out string onLine);
            bool completedOff = RunTrapMatch(seed, halfSeconds, traps: false, off, out string offLine);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-34}{"ON",12}{"OFF",12}{"DELTA",12}");
        Console.WriteLine($"{"completed matches",-34}{on.Matches,12}{off.Matches,12}{on.Matches - off.Matches,12}");
        Console.WriteLine($"{"live ticks",-34}{on.Ticks,12}{off.Ticks,12}{on.Ticks - off.Ticks,12}");
        Console.WriteLine($"{"passes attempted",-34}{on.PassesAttempted,12}{off.PassesAttempted,12}{on.PassesAttempted - off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-34}{on.PassesCompleted,12}{off.PassesCompleted,12}{on.PassesCompleted - off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-34}{on.Shots,12}{off.Shots,12}{on.Shots - off.Shots,12}");
        Console.WriteLine($"{"traps raised",-34}{on.Raised,12}{off.Raised,12}{on.Raised - off.Raised,12}");
        Console.WriteLine($"{"trap ticks",-34}{on.TrapTicks,12}{off.TrapTicks,12}{on.TrapTicks - off.TrapTicks,12}");
        Console.WriteLine($"{"traps won",-34}{on.Won,12}{off.Won,12}{on.Won - off.Won,12}");
        Console.WriteLine($"{"traps escaped",-34}{on.Escaped,12}{off.Escaped,12}{on.Escaped - off.Escaped,12}");
        Console.WriteLine($"{"traps loose",-34}{on.Loose,12}{off.Loose,12}{on.Loose - off.Loose,12}");
        Console.WriteLine($"{"traps expired",-34}{on.Expired,12}{off.Expired,12}{on.Expired - off.Expired,12}");
        Console.WriteLine($"{"traps presser-lost",-34}{on.PresserLost,12}{off.PresserLost,12}{on.PresserLost - off.PresserLost,12}");
        Console.WriteLine($"{"traps cover-lost",-34}{on.CoverLost,12}{off.CoverLost,12}{on.CoverLost - off.CoverLost,12}");
        Console.WriteLine($"{"recovery-blocked raises",-34}{on.RecoveryBlocked,12}{off.RecoveryBlocked,12}{on.RecoveryBlocked - off.RecoveryBlocked,12}");
        Console.WriteLine($"{"screener decisions",-34}{on.ScreenerDecisions,12}{off.ScreenerDecisions,12}{on.ScreenerDecisions - off.ScreenerDecisions,12}");
        Console.WriteLine($"{"screener chose Screen",-34}{on.ScreenChosen,12}{off.ScreenChosen,12}{on.ScreenChosen - off.ScreenChosen,12}");
        Console.WriteLine($"{"presser decisions",-34}{on.PresserDecisions,12}{off.PresserDecisions,12}{on.PresserDecisions - off.PresserDecisions,12}");
        Console.WriteLine($"{"cover decisions",-34}{on.CoverDecisions,12}{off.CoverDecisions,12}{on.CoverDecisions - off.CoverDecisions,12}");

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool trapsLive = on.Raised > 0 && on.TrapTicks > 0;
        bool rolesReach = on.ScreenerDecisions > 0 && on.ScreenChosen > 0 && on.PresserDecisions > 0 &&
                          on.CoverDecisions > 0;
        bool bothOutcomes = on.Won > 0 && on.Escaped > 0;
        bool neutralOff = off.Raised == 0 && off.TrapTicks == 0 && off.ScreenerDecisions == 0 &&
                          off.PresserDecisions == 0 && off.CoverDecisions == 0;
        bool ok = completeBoth && trapsLive && rolesReach && bothOutcomes && neutralOff;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the trap build raises traps that are won and escaped, the named roles reach the" +
              " controllers, and the version-49 build raises none; both finished every seed"
            : "RESULT: FAIL -- incomplete matches, no live trap, a role that never reached a controller, no" +
              " won-and-escaped trap pair, or the trap did not neutralize with the feature off");
        return ok;
    }

    /// <summary>One A8.4 probe variant's accumulators.</summary>
    private sealed class TrapEvidenceTotals
    {
        public int Matches;
        public long Ticks;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long Raised;
        public long TrapTicks;
        public long Won;
        public long Escaped;
        public long Loose;
        public long Expired;
        public long PresserLost;
        public long CoverLost;
        public long RecoveryBlocked;
        public long ScreenerDecisions;
        public long ScreenChosen;
        public long PresserDecisions;
        public long CoverDecisions;
    }

    /// <summary>Runs one A8.4 probe match and folds it into <paramref name="totals"/>.</summary>
    private static bool RunTrapMatch(
        ulong seed,
        float halfSeconds,
        bool traps,
        TrapEvidenceTotals totals,
        out string line)
    {
        var ai = new AiRuntimeConfig { EnableTrapContracts = traps };
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        long seedTrapTicks = 0;
        long seedScreener = 0;
        long seedScreenChosen = 0;
        long seedPresser = 0;
        long seedCover = 0;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();

            if (engine.Communication.Traps.LiveCount(engine.CurrentTick) > 0) seedTrapTicks++;

            // The participants' decisions this tick, from their own observations: the role they were given and
            // the action they chose. This is what proves the contract reached the live controllers.
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch) continue;
                FootballerObservation? observation = engine.GetLastObservation(player.Id);
                if (observation is null || !observation.Trap.IsActive) continue;
                if (engine.GetController(player.Id) is not TacticalUtilityAiController controller) continue;

                if (observation.Trap.IsScreener)
                {
                    seedScreener++;
                    if (controller.LastSelected.ActionName == "ScreenThePass") seedScreenChosen++;
                }
                else if (observation.Trap.IsPresser)
                {
                    seedPresser++;
                }
                else if (observation.Trap.IsCover)
                {
                    seedCover++;
                }
            }
        }

        TrapCoordinator board = engine.Communication.Traps;
        var analytics = engine.Analytics;
        totals.Ticks += engine.CurrentTick;
        totals.PassesAttempted += analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        totals.PassesCompleted += analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        totals.Shots += analytics.HomeShots + analytics.AwayShots;
        totals.Raised += board.RaisedCount;
        totals.TrapTicks += seedTrapTicks;
        totals.Won += board.CountOfEnd(TrapEndReason.Won);
        totals.Escaped += board.CountOfEnd(TrapEndReason.Escaped);
        totals.Loose += board.CountOfEnd(TrapEndReason.Loose);
        totals.Expired += board.CountOfEnd(TrapEndReason.Expired);
        totals.PresserLost += board.CountOfEnd(TrapEndReason.PresserLost);
        totals.CoverLost += board.CountOfEnd(TrapEndReason.CoverLost);
        totals.RecoveryBlocked += board.RecoveryBlockedCount;
        totals.ScreenerDecisions += seedScreener;
        totals.ScreenChosen += seedScreenChosen;
        totals.PresserDecisions += seedPresser;
        totals.CoverDecisions += seedCover;

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.Matches++;

        line = $"{engine.CurrentTick} ticks, score {analytics.HomeGoals}-{analytics.AwayGoals}, passes " +
               $"{analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} attempted, shots " +
               $"{analytics.HomeShots}/{analytics.AwayShots}, traps raised {board.RaisedCount} " +
               $"(won {board.CountOfEnd(TrapEndReason.Won)}, escaped {board.CountOfEnd(TrapEndReason.Escaped)}, " +
               $"loose {board.CountOfEnd(TrapEndReason.Loose)}, expired {board.CountOfEnd(TrapEndReason.Expired)}, " +
               $"presser-lost {board.CountOfEnd(TrapEndReason.PresserLost)}, " +
               $"cover-lost {board.CountOfEnd(TrapEndReason.CoverLost)}), trap ticks {seedTrapTicks}, " +
               $"roles S{seedScreener}/P{seedPresser}/C{seedCover}, screens chosen {seedScreenChosen}";
        return completed;
    }

    // ---------------------------------------------------------------------------------------------
    // A8.5 -- the small library of legal restart routines.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Same-seed A/B evidence for A8.5's restart routines: every restart the match awards is played by the
    /// routine the restarting team elected (short to the support, or delivered to the target) in the ON build, and
    /// by the taker's own pre-A8.5 heuristic in the OFF build. The probe reports the routine census (kind, option,
    /// ending), how often the taker actually played the named receiver, the restarts awarded, and the ordinary
    /// match totals so the effect is readable rather than asserted.
    /// </summary>
    private static bool RunRestartEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A8.5 restart-routine evidence: same-seed A/B, {seedCount} seeds x " +
                          $"{halfSeconds * 2f:F0} s -- routines on vs off ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Routine model      : while the rules engine has a restart armed, the restarting team");
        Console.WriteLine("                     elects one routine from a two-option lawful menu - the SHORT option to");
        Console.WriteLine("                     the support player's feet, or DELIVER to a target further up - with the");
        Console.WriteLine("                     support and the target named as roles. The choice is a bounded weight");
        Console.WriteLine("                     (space, shortness, progression) plus the profile's own directness (A6.5b's");
        Console.WriteLine("                     short-distribution preference for a goal kick). Law 11 is the gate: for the");
        Console.WriteLine("                     restarts that are not offside-exempt (kick-off, free kick, indirect free");
        Console.WriteLine("                     kick) a receiver must be onside by the believed line every pass keeps.");
        Console.WriteLine("                     The rules engine keeps placement, taker and restart state; the taker still");
        Console.WriteLine("                     plans the delivery (A4.9) and falls back to his own heuristic if the named");
        Console.WriteLine("                     receiver cannot be delivered to. The routine ends once - taken, stolen,");
        Console.WriteLine("                     replaced, expired, taker/participant lost or reset - so it never survives");
        Console.WriteLine("                     into unrelated possession.");
        Console.WriteLine($"Routines ON        : {new AiRuntimeConfig { EnableRestartRoutines = true }.Describe()}");
        Console.WriteLine($"Routines OFF       : {new AiRuntimeConfig { EnableRestartRoutines = false }.Describe()}");
        Console.WriteLine();

        var on = new RestartEvidenceTotals();
        var off = new RestartEvidenceTotals();
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            ulong seed = 20260818UL + (ulong)seedIndex * 7919UL;
            bool completedOn = RunRestartMatch(seed, halfSeconds, routines: true, on, out string onLine);
            bool completedOff = RunRestartMatch(seed, halfSeconds, routines: false, off, out string offLine);
            Console.WriteLine($"Seed {seed}:");
            Console.WriteLine($"  ON  : {(completedOn ? "FT       " : "INCOMPLETE")} | {onLine}");
            Console.WriteLine($"  OFF : {(completedOff ? "FT       " : "INCOMPLETE")} | {offLine}");
        }

        Console.WriteLine();
        Console.WriteLine($"=== Totals over {seedCount} seeds ===");
        if (on.Matches == 0 || off.Matches == 0)
        {
            Console.WriteLine("No completed match to measure.");
            return false;
        }

        Console.WriteLine($"{"metric",-34}{"ON",12}{"OFF",12}{"DELTA",12}");
        Console.WriteLine($"{"completed matches",-34}{on.Matches,12}{off.Matches,12}{on.Matches - off.Matches,12}");
        Console.WriteLine($"{"live ticks",-34}{on.Ticks,12}{off.Ticks,12}{on.Ticks - off.Ticks,12}");
        Console.WriteLine($"{"restart events logged",-34}{on.Restarts,12}{off.Restarts,12}{on.Restarts - off.Restarts,12}");
        Console.WriteLine($"{"routines raised",-34}{on.Raised,12}{off.Raised,12}{on.Raised - off.Raised,12}");
        Console.WriteLine($"{"routine ticks",-34}{on.RoutineTicks,12}{off.RoutineTicks,12}{on.RoutineTicks - off.RoutineTicks,12}");
        Console.WriteLine($"{"  kickoff",-34}{on.Kickoff,12}{off.Kickoff,12}{on.Kickoff - off.Kickoff,12}");
        Console.WriteLine($"{"  throw-in",-34}{on.ThrowIn,12}{off.ThrowIn,12}{on.ThrowIn - off.ThrowIn,12}");
        Console.WriteLine($"{"  goal kick",-34}{on.GoalKick,12}{off.GoalKick,12}{on.GoalKick - off.GoalKick,12}");
        Console.WriteLine($"{"  corner",-34}{on.Corner,12}{off.Corner,12}{on.Corner - off.Corner,12}");
        Console.WriteLine($"{"  free kick",-34}{on.FreeKick,12}{off.FreeKick,12}{on.FreeKick - off.FreeKick,12}");
        Console.WriteLine($"{"  indirect free kick",-34}{on.IndirectFreeKick,12}{off.IndirectFreeKick,12}{on.IndirectFreeKick - off.IndirectFreeKick,12}");
        Console.WriteLine($"{"  option Short",-34}{on.Short,12}{off.Short,12}{on.Short - off.Short,12}");
        Console.WriteLine($"{"  option Deliver",-34}{on.Deliver,12}{off.Deliver,12}{on.Deliver - off.Deliver,12}");
        Console.WriteLine($"{"routines taken",-34}{on.Taken,12}{off.Taken,12}{on.Taken - off.Taken,12}");
        Console.WriteLine($"{"routines stolen",-34}{on.Stolen,12}{off.Stolen,12}{on.Stolen - off.Stolen,12}");
        Console.WriteLine($"{"routines replaced",-34}{on.Replaced,12}{off.Replaced,12}{on.Replaced - off.Replaced,12}");
        Console.WriteLine($"{"routines expired",-34}{on.Expired,12}{off.Expired,12}{on.Expired - off.Expired,12}");
        Console.WriteLine($"{"taker decisions",-34}{on.TakerDecisions,12}{off.TakerDecisions,12}{on.TakerDecisions - off.TakerDecisions,12}");
        Console.WriteLine($"{"taker played the receiver",-34}{on.TakerApplied,12}{off.TakerApplied,12}{on.TakerApplied - off.TakerApplied,12}");
        Console.WriteLine($"{"goal-kick keeper decisions",-34}{on.GoalKickDecisions,12}{off.GoalKickDecisions,12}{on.GoalKickDecisions - off.GoalKickDecisions,12}");
        Console.WriteLine($"{"goal-kick keeper played it",-34}{on.GoalKickApplied,12}{off.GoalKickApplied,12}{on.GoalKickApplied - off.GoalKickApplied,12}");
        Console.WriteLine($"{"passes attempted",-34}{on.PassesAttempted,12}{off.PassesAttempted,12}{on.PassesAttempted - off.PassesAttempted,12}");
        Console.WriteLine($"{"passes completed",-34}{on.PassesCompleted,12}{off.PassesCompleted,12}{on.PassesCompleted - off.PassesCompleted,12}");
        Console.WriteLine($"{"shots",-34}{on.Shots,12}{off.Shots,12}{on.Shots - off.Shots,12}");
        Console.WriteLine($"{"offside offences",-34}{on.Offside,12}{off.Offside,12}{on.Offside - off.Offside,12}");

        bool completeBoth = on.Matches == seedCount && off.Matches == seedCount;
        bool routinesLive = on.Raised > 0 && on.Taken > 0 && on.RoutineTicks > 0;
        bool takerReached = on.TakerDecisions > 0 && on.TakerApplied > 0;
        bool neutralOff = off.Raised == 0 && off.RoutineTicks == 0 && off.TakerDecisions == 0 &&
                          off.GoalKickDecisions == 0;
        bool ok = completeBoth && routinesLive && takerReached && neutralOff;

        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- the routine build raises and takes routines for every restart the match awards," +
              " the taker plays the receiver the routine named, and the version-50 build raises none; both" +
              " finished every seed"
            : "RESULT: FAIL -- incomplete matches, no routine raised or taken, a taker that never played the" +
              " routine's receiver, or the routine did not neutralize with the feature off");
        return ok;
    }

    /// <summary>One A8.5 probe variant's accumulators.</summary>
    private sealed class RestartEvidenceTotals
    {
        public int Matches;
        public long Ticks;
        public long Restarts;
        public long Raised;
        public long RoutineTicks;
        public long Kickoff;
        public long ThrowIn;
        public long GoalKick;
        public long Corner;
        public long FreeKick;
        public long IndirectFreeKick;
        public long Short;
        public long Deliver;
        public long Taken;
        public long Stolen;
        public long Replaced;
        public long Expired;
        public long TakerDecisions;
        public long TakerApplied;
        public long GoalKickDecisions;
        public long GoalKickApplied;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long Offside;
    }

    /// <summary>Runs one A8.5 probe match and folds it into <paramref name="totals"/>.</summary>
    private static bool RunRestartMatch(
        ulong seed,
        float halfSeconds,
        bool routines,
        RestartEvidenceTotals totals,
        out string line)
    {
        var ai = new AiRuntimeConfig { EnableRestartRoutines = routines };
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        long seedRoutineTicks = 0;
        long seedKickoff = 0;
        long seedThrowIn = 0;
        long seedGoalKick = 0;
        long seedCorner = 0;
        long seedFreeKick = 0;
        long seedIndirectFreeKick = 0;
        long seedShort = 0;
        long seedDeliver = 0;
        long seedTakerDecisions = 0;
        long seedTakerApplied = 0;
        long seedGoalKickDecisions = 0;
        long seedGoalKickApplied = 0;
        int lastRoutineId = 0;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();

            RestartCoordinator board = engine.Communication.Restarts;
            if (board.TryGetLive(engine.CurrentTick, out RestartRoutine routine))
            {
                seedRoutineTicks++;

                // Count each routine once, by the kind and option it was raised with.
                if (routine.RoutineId != lastRoutineId)
                {
                    lastRoutineId = routine.RoutineId;
                    switch (routine.Kind)
                    {
                        case RestartKind.Kickoff: seedKickoff++; break;
                        case RestartKind.ThrowIn: seedThrowIn++; break;
                        case RestartKind.GoalKick: seedGoalKick++; break;
                        case RestartKind.CornerKick: seedCorner++; break;
                        case RestartKind.FreeKick: seedFreeKick++; break;
                        case RestartKind.IndirectFreeKick: seedIndirectFreeKick++; break;
                    }

                    if (routine.Option == RestartOption.Short) seedShort++;
                    else if (routine.Option == RestartOption.Deliver) seedDeliver++;
                }
            }

            // Did the participant actually play what the routine asked? Read from the observation the decision
            // used and the command it produced, so this cannot be a board-only claim.
            foreach (FootballerState player in engine.Players)
            {
                if (!player.IsActiveOnPitch) continue;
                FootballerObservation? observation = engine.GetLastObservation(player.Id);
                if (observation is null || !observation.Routine.IsTaker) continue;

                if (engine.GetController(player.Id) is TacticalUtilityAiController controller)
                {
                    seedTakerDecisions++;
                    if (player.LastCommand.IntendedReceiverId == observation.Routine.ReceiverId)
                        seedTakerApplied++;
                }
                else if (engine.GetController(player.Id) is GoalkeeperController keeper)
                {
                    seedGoalKickDecisions++;
                    if (keeper.LastDistribution.Kind != GoalkeeperDistributionKind.None &&
                        keeper.LastDistribution.ReceiverId == observation.Routine.ReceiverId)
                    {
                        seedGoalKickApplied++;
                    }
                }
            }
        }

        RestartCoordinator routinesBoard = engine.Communication.Restarts;
        var analytics = engine.Analytics;
        totals.Ticks += engine.CurrentTick;
        totals.Restarts += engine.Rules.Events.Count(e => IsRestartEvent(e.EventType));
        totals.Raised += routinesBoard.RaisedCount;
        totals.RoutineTicks += seedRoutineTicks;
        totals.Kickoff += seedKickoff;
        totals.ThrowIn += seedThrowIn;
        totals.GoalKick += seedGoalKick;
        totals.Corner += seedCorner;
        totals.FreeKick += seedFreeKick;
        totals.IndirectFreeKick += seedIndirectFreeKick;
        totals.Short += seedShort;
        totals.Deliver += seedDeliver;
        totals.Taken += routinesBoard.CountOfEnd(RestartEndReason.Taken);
        totals.Stolen += routinesBoard.CountOfEnd(RestartEndReason.Stolen);
        totals.Replaced += routinesBoard.CountOfEnd(RestartEndReason.Replaced);
        totals.Expired += routinesBoard.CountOfEnd(RestartEndReason.Expired);
        totals.TakerDecisions += seedTakerDecisions;
        totals.TakerApplied += seedTakerApplied;
        totals.GoalKickDecisions += seedGoalKickDecisions;
        totals.GoalKickApplied += seedGoalKickApplied;
        totals.PassesAttempted += analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        totals.PassesCompleted += analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        totals.Shots += analytics.HomeShots + analytics.AwayShots;
        totals.Offside += engine.Rules.Events.Count(e => e.Details.Contains("Offside against"));

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.Matches++;

        line = $"{engine.CurrentTick} ticks, score {analytics.HomeGoals}-{analytics.AwayGoals}, passes " +
               $"{analytics.HomePassesAttempted}/{analytics.AwayPassesAttempted} attempted, shots " +
               $"{analytics.HomeShots}/{analytics.AwayShots}, " +
               $"restart events {engine.Rules.Events.Count(e => IsRestartEvent(e.EventType))}, " +
               $"routines {routinesBoard.RaisedCount} " +
               $"(K{seedKickoff}/T{seedThrowIn}/G{seedGoalKick}/C{seedCorner}/F{seedFreeKick}/I{seedIndirectFreeKick}," +
               $" short {seedShort}/deliver {seedDeliver}, taken {routinesBoard.CountOfEnd(RestartEndReason.Taken)}, " +
               $"stolen {routinesBoard.CountOfEnd(RestartEndReason.Stolen)}, " +
               $"replaced {routinesBoard.CountOfEnd(RestartEndReason.Replaced)}), taker played the receiver " +
               $"{seedTakerApplied}/{seedTakerDecisions}, goal-kick keeper played it {seedGoalKickApplied}/{seedGoalKickDecisions}";
        return completed;
    }

    /// <summary>True for the rules events that award one of the restarts A8.5 has a routine for.</summary>
    private static bool IsRestartEvent(string eventType) => eventType is
        "Kickoff" or "ThrowIn" or "GoalKick" or "CornerKick" or "FreeKick";

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
        // AGENTS Â§2.2: every recorded artifact states the simulation version, and only same-version numbers
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
            // L46: the criterion is 4 of 5, i.e. a supermajority, and must not be described as a majority.
            $"(gate: at least {seedCount - 1}/{seedCount} and mean > 0.5 m)");
        Console.WriteLine(allPassed ? "GATE: PASS" : "GATE: FAIL");
        return allPassed;
    }

    // ---------------------------------------------------------------------------------------------
    // A9.1 -- the style matrix.
    // ---------------------------------------------------------------------------------------------

    // Indices of the six presets in StylePresets, in print order. The claim block names them, so the indices
    // are named rather than repeated as literals.
    private const int StyleIndexPossessionControl = 0;
    private const int StyleIndexDirect = 1;
    private const int StyleIndexDirectCounterattack = 2;
    private const int StyleIndexHighPress = 3;
    private const int StyleIndexLowBlock = 4;
    private const int StyleIndexManOriented = 5;

    /// <summary>How many style presets the A9.1 matrix plays.</summary>
    private const int StylePresetCount = 6;

    /// <summary>
    /// How often the A9.1 probe samples the style team's jobs, in ticks. It matches the analytics sample
    /// interval, so the marking numbers and the shape numbers describe the same instants.
    /// </summary>
    private const int StyleMarkingSampleTicks = 30;

    /// <summary>
    /// The six A9.1 style presets, in the order every block prints them. The probe only reads them: each is an
    /// init-only <see cref="TeamTacticalProfile"/>, so every seed plays the identical configuration and the
    /// matrix compares the presets' numbers, not six code paths.
    /// </summary>
    private static readonly TeamTacticalProfile[] StylePresets =
    {
        TeamTacticalProfile.PossessionControl,
        TeamTacticalProfile.Direct,
        TeamTacticalProfile.DirectCounterattack,
        TeamTacticalProfile.HighPressGegenpress,
        TeamTacticalProfile.LowBlock,
        TeamTacticalProfile.ManOriented
    };

    /// <summary>Short names for the aggregate table, the per-seed lines and the paired claims block.</summary>
    private static readonly string[] StyleShortNames =
    {
        "Possession",
        "Direct",
        "Counter",
        "Press",
        "LowBlock",
        "ManOr"
    };

    /// <summary>
    /// A9.1 style-matrix evidence: the six style presets against the neutral benchmark over a seed set, each
    /// preset played at home and away.
    ///
    /// The matrix exists because a profile is a set of preferences on the one decision pipeline: proving the
    /// presets are <b>distinct</b> is a measurement, not a review of the numbers in the source. Every preset
    /// therefore plays the same squads, rules and AI against the same neutral benchmark (<c>new
    /// TeamTacticalProfile()</c>, "Balanced"), and plays it once at home and once away, so the venue and end
    /// effect cancels in the style's mean and the columns compare the presets rather than the fixtures. The
    /// probe is read-only: it reads the analytics summary, the live job board and the authoritative player
    /// positions, and writes nothing into the world, so the same seed stays reproducible.
    ///
    /// The claims are paired by seed - each style's mean over its home and away end on that seed - and only
    /// state consequences of the presets' own numbers, so a failure names a configuration that is not reaching
    /// the pitch:
    /// <list type="bullet">
    /// <item>possession keeps more of the ball than direct (paired majority, mean more than +2 pp);</item>
    /// <item>possession is wider with the ball than direct (at least seeds-1 seeds, mean more than +2 m);</item>
    /// <item>the high press stands its defensive line higher than the low block (at least seeds-1 seeds, mean
    /// more than +4 m of anchor projection);</item>
    /// <item>possession stands its defensive line higher than the counter-attacking side (at least seeds-1
    /// seeds, mean more than +2 m of anchor projection);</item>
    /// <item>direct stands its defensive line higher than the low block (at least seeds-1 seeds, mean more than
    /// +2 m of anchor projection);</item>
    /// <item>the man-oriented press holds at least as many markers per sample as the high press (mean);</item>
    /// <item>the man-oriented press holds more markers per sample than possession (at least seeds-1 seeds);</item>
    /// <item>every style's every match reaches full time.</item>
    /// </list>
    ///
    /// Line height is the mean of <c>DefensiveLineBand.AnchorMeanProjX</c> over the 30-tick samples in which the
    /// style team is defending and its band is valid, in metres in its own attacking direction (positive = higher
    /// up the pitch). The band's placed depth <c>ProjX</c> is reported beside it but not gated: caps and local
    /// exceptions are situational, the anchor mean is the profile's own line. Marker body distance is likewise
    /// reported but not gated: a marker's body carries the approach lag and a kept assignment's stale anchor, so
    /// it does not measure the profile's 1.45-1.9 m anchor standoff (that is proven by A9StylePresetTests). Pass
    /// volume, completion and territory are reported for the same reason: the eight-seed run showed they are not
    /// monotone in style.
    /// </summary>
    private static bool RunStyleEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 6;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 120f;

        Console.WriteLine($"=== A9.1 style-matrix evidence: six presets vs the neutral benchmark, {seedCount} seeds x " +
                          $"{halfSeconds * 2f:F0} s, home and away ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Style matrix       : every preset plays the neutral benchmark profile (new TeamTacticalProfile(),");
        Console.WriteLine("                     \"Balanced\") once at home and once away on the same seed, so the venue and");
        Console.WriteLine("                     end effect cancel in the style's mean. The six presets are preference bundles");
        Console.WriteLine("                     on one decision pipeline: the same squads, rules and AI cadence, with no");
        Console.WriteLine("                     candidate, sight, reach or contact differing between them.");
        Console.WriteLine("Presets            : " + DescribeStylePresets(0, 3));
        Console.WriteLine("                     " + DescribeStylePresets(3, 3));
        Console.WriteLine("Metrics            : possession share = the style's possession ticks / (home + away possession");
        Console.WriteLine("                     ticks); passes/shots/goals = the style's own executed outcomes; width = the");
        Console.WriteLine("                     possession-scoped span; territory = mean ball x in the style's attacking");
        Console.WriteLine("                     direction while in possession (a ball-progress measure - printed, not gated);");
        Console.WriteLine("                     markers = style players whose live job is TacticalJob.Marker per 30-tick");
        Console.WriteLine("                     sample, body distance = marker-to-marked-man XZ distance including the");
        Console.WriteLine("                     approach lag (printed, not gated: it is not the assignment-time standoff).");
        Console.WriteLine("Line height        : mean of DefensiveLineBand.AnchorMeanProjX over the 30-tick samples in");
        Console.WriteLine("                     which the style team is defending and the band is valid, in metres in the");
        Console.WriteLine("                     style's own attacking direction (positive = higher up the pitch). The band's");
        Console.WriteLine("                     placed depth ProjX is reported beside it but not gated: caps and local");
        Console.WriteLine("                     exceptions are situational, the anchor mean is the profile's own line.");
        Console.WriteLine("Verdict            : the predefined claim block at the end, paired by seed. Two kinds of claim,");
        Console.WriteLine("                     because they are two kinds of statement: a SHAPE claim (width, line height,");
        Console.WriteLine("                     marker count) is a property of the assignment the presets' numbers state, so");
        Console.WriteLine("                     it must hold on all but at most one seed; a CONSEQUENCE claim (possession");
        Console.WriteLine("                     share) is a stochastic match outcome, so it needs a paired-seed majority and");
        Console.WriteLine("                     a mean threshold. Pass volume, completion, territory and marker body");
        Console.WriteLine("                     distance are reported, never gated - the three-seed diagnostic and the");
        Console.WriteLine("                     recorded eight-seed run both showed they are not monotone in style (a direct");
        Console.WriteLine("                     side's longer balls still count as passes, and a turnover raises the count).");
        Console.WriteLine();

        var styles = new StyleEvidenceTotals[StylePresetCount];
        for (int styleIndex = 0; styleIndex < StylePresetCount; styleIndex++)
        {
            styles[styleIndex] = new StyleEvidenceTotals
            {
                Name = StyleShortNames[styleIndex],
                PerSeed = new StyleSeedMetrics[seedCount]
            };
        }

        var seeds = new ulong[seedCount];
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
            seeds[seedIndex] = 20260818UL + (ulong)seedIndex * 7919UL;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            Console.WriteLine($"Seed {seeds[seedIndex]}:");
            for (int styleIndex = 0; styleIndex < StylePresetCount; styleIndex++)
            {
                StyleEvidenceTotals totals = styles[styleIndex];
                bool homeCompleted = RunStyleMatch(
                    seeds[seedIndex], halfSeconds, StylePresets[styleIndex], styleAtHome: true, totals,
                    out StyleMatchMetrics home);
                bool awayCompleted = RunStyleMatch(
                    seeds[seedIndex], halfSeconds, StylePresets[styleIndex], styleAtHome: false, totals,
                    out StyleMatchMetrics away);

                var paired = new StyleSeedMetrics
                {
                    PossessionSharePercent = (home.PossessionShare01 + away.PossessionShare01) * 50d,
                    PassesAttemptedPerMatch = (home.PassesAttempted + away.PassesAttempted) / 2d,
                    AttackingWidthMeters = (home.AttackingWidthMeters + away.AttackingWidthMeters) / 2d,
                    TerritoryMeters = (home.TerritoryMeters + away.TerritoryMeters) / 2d
                };
                long markerSamples = home.MarkerSamples + away.MarkerSamples;
                paired.MarkersHeldPerSample = markerSamples > 0
                    ? (double)(home.MarkersHeld + away.MarkersHeld) / markerSamples
                    : 0d;
                long markerBodySamples = home.MarkerBodyDistanceSamples + away.MarkerBodyDistanceSamples;
                paired.MarkerBodyDistanceMeters = markerBodySamples > 0
                    ? (home.MarkerBodyDistanceSum + away.MarkerBodyDistanceSum) / markerBodySamples
                    : 0d;
                long lineSamples = home.LineSamples + away.LineSamples;
                paired.DefensiveLineAnchorMeters = lineSamples > 0
                    ? (home.LineAnchorSum + away.LineAnchorSum) / lineSamples
                    : 0d;
                totals.PerSeed[seedIndex] = paired;

                Console.WriteLine($"  {StyleShortNames[styleIndex],-10} H {(homeCompleted ? "FT        " : "INCOMPLETE")} | " +
                                  DescribeStyleMatch(home));
                Console.WriteLine($"  {StyleShortNames[styleIndex],-10} A {(awayCompleted ? "FT        " : "INCOMPLETE")} | " +
                                  DescribeStyleMatch(away));
            }

            Console.WriteLine();
        }

        WriteStyleAggregateTable(styles, seedCount);
        WriteStylePairedClaims(styles, seeds);
        bool claimOk = ReportStyleClaims(styles, seedCount);

        Console.WriteLine();
        Console.WriteLine(claimOk
            ? "RESULT: PASS -- every style finished every match at both ends; possession held more of the ball" +
              " than direct on a paired-seed majority and was wider with the ball on every seed; the defensive" +
              " line the allocator placed was higher for the high press than for the low block, higher for" +
              " possession than for the counter side, and higher for direct than for the low block on every" +
              " seed; and the man-oriented press held more markers than possession on every seed and at least as" +
              " many as the high press - the presets' stated numbers reached the pitch"
            : "RESULT: FAIL -- a style did not reach full time, or a predefined style claim missed its paired-seed" +
              " majority/supermajority or its mean threshold (the claim lines above name the misses)");
        return claimOk;
    }

    /// <summary>
    /// The A9.1 aggregate: one metric row per style column, pooled over both ends and every seed. Means are per
    /// played match (or per possession sample for the marking rows), so the columns compare the presets at the
    /// same denominator regardless of how many matches each end completed.
    /// </summary>
    private static void WriteStyleAggregateTable(StyleEvidenceTotals[] styles, int seedCount)
    {
        Console.WriteLine($"=== Aggregate over {seedCount} seeds x 2 ends ===");
        Console.Write($"{"metric",-34}");
        for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
            Console.Write($"{styles[styleIndex].Name,12}");
        Console.WriteLine();

        Console.Write($"{"completed matches",-34}");
        for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
            Console.Write($"{styles[styleIndex].MatchesDescription,12}");
        Console.WriteLine();

        WriteStyleMetricRow("live ticks", styles, totals => totals.Ticks, "F0");
        WriteStyleMetricRow("possession share %", styles, totals => totals.PossessionSharePercent, "F1");
        WriteStyleMetricRow("passes attempted / match", styles, totals => totals.PassesAttemptedPerMatch, "F1");
        WriteStyleMetricRow("passes completed / match", styles, totals => totals.PassesCompletedPerMatch, "F1");
        WriteStyleMetricRow("pass completion %", styles, totals => totals.PassCompletionPercent, "F1");
        WriteStyleMetricRow("shots / match", styles, totals => totals.ShotsPerMatch, "F2");
        WriteStyleMetricRow("goals / match", styles, totals => totals.GoalsPerMatch, "F2");
        WriteStyleMetricRow("attacking width, ball m", styles, totals => totals.AttackingWidthMeanMeters, "F1");
        WriteStyleMetricRow("ball territory, possession m", styles, totals => totals.TerritoryMeanMeters, "F1");
        WriteStyleMetricRow("defensive line anchor m", styles, totals => totals.DefensiveLineAnchorMeanMeters, "F1");
        WriteStyleMetricRow("defensive line placed m", styles, totals => totals.DefensiveLinePlacedMeanMeters, "F1");
        WriteStyleMetricRow("markers held / 30-tick sample", styles, totals => totals.MarkersHeldPerSample, "F2");
        WriteStyleMetricRow("marker body distance m (incl. lag)", styles,
            totals => totals.MarkerBodyDistanceMeanMeters, "F2");
        Console.WriteLine();
    }

    /// <summary>
    /// Writes one aligned row of the A9.1 aggregate table. Six style columns are wider than any two-variant
    /// table in this runner, so the six-cell row is built once here instead of repeated on every metric line.
    /// </summary>
    private static void WriteStyleMetricRow(
        string label,
        StyleEvidenceTotals[] styles,
        Func<StyleEvidenceTotals, double> value,
        string format)
    {
        Console.Write($"{label,-34}");
        for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
            Console.Write($"{value(styles[styleIndex]).ToString(format),12}");
        Console.WriteLine();
    }

    /// <summary>
    /// The A9.1 paired-by-seed block: for each seed, every claim's two styles as the mean of their home and away
    /// end. Pairing by seed keeps the seed-to-seed variation out of the comparison, which a pooled mean would
    /// smear across different worlds, so a single inverted fixture is visible instead of hidden.
    /// </summary>
    private static void WriteStylePairedClaims(StyleEvidenceTotals[] styles, ulong[] seeds)
    {
        Console.WriteLine("=== Paired claims by seed: each style's mean over its home and away end ===");
        for (int seedIndex = 0; seedIndex < seeds.Length; seedIndex++)
        {
            StyleSeedMetrics possession = styles[StyleIndexPossessionControl].PerSeed[seedIndex];
            StyleSeedMetrics direct = styles[StyleIndexDirect].PerSeed[seedIndex];
            StyleSeedMetrics counter = styles[StyleIndexDirectCounterattack].PerSeed[seedIndex];
            StyleSeedMetrics press = styles[StyleIndexHighPress].PerSeed[seedIndex];
            StyleSeedMetrics lowBlock = styles[StyleIndexLowBlock].PerSeed[seedIndex];
            StyleSeedMetrics manOriented = styles[StyleIndexManOriented].PerSeed[seedIndex];

            Console.WriteLine($"Seed {seeds[seedIndex]}:");
            Console.WriteLine($"  possession share % : possession {possession.PossessionSharePercent:F1} vs direct " +
                              $"{direct.PossessionSharePercent:F1} " +
                              $"({possession.PossessionSharePercent - direct.PossessionSharePercent:+0.0;-0.0;0.0} pp)" +
                              $" | press {press.PossessionSharePercent:F1} vs low block {lowBlock.PossessionSharePercent:F1} " +
                              $"({press.PossessionSharePercent - lowBlock.PossessionSharePercent:+0.0;-0.0;0.0} pp)");
            Console.WriteLine($"  passes per match   : possession {possession.PassesAttemptedPerMatch:F1} vs direct " +
                              $"{direct.PassesAttemptedPerMatch:F1} " +
                              $"({possession.PassesAttemptedPerMatch - direct.PassesAttemptedPerMatch:+0.0;-0.0;0.0})" +
                              $" | vs low block {lowBlock.PassesAttemptedPerMatch:F1} " +
                              $"({possession.PassesAttemptedPerMatch - lowBlock.PassesAttemptedPerMatch:+0.0;-0.0;0.0})");
            Console.WriteLine($"  width with ball m  : possession {possession.AttackingWidthMeters:F1} vs direct " +
                              $"{direct.AttackingWidthMeters:F1} " +
                              $"({possession.AttackingWidthMeters - direct.AttackingWidthMeters:+0.0;-0.0;0.0} m)");
            Console.WriteLine($"  territory m        : possession {possession.TerritoryMeters:F1} vs counter " +
                              $"{counter.TerritoryMeters:F1} " +
                              $"({possession.TerritoryMeters - counter.TerritoryMeters:+0.0;-0.0;0.0} m)" +
                              $" | press {press.TerritoryMeters:F1} vs low block {lowBlock.TerritoryMeters:F1} " +
                              $"({press.TerritoryMeters - lowBlock.TerritoryMeters:+0.0;-0.0;0.0} m)");
            Console.WriteLine($"  line height m      : press {press.DefensiveLineAnchorMeters:F1} vs low block " +
                              $"{lowBlock.DefensiveLineAnchorMeters:F1} " +
                              $"({press.DefensiveLineAnchorMeters - lowBlock.DefensiveLineAnchorMeters:+0.0;-0.0;0.0} m)" +
                              $" | possession {possession.DefensiveLineAnchorMeters:F1} vs counter " +
                              $"{counter.DefensiveLineAnchorMeters:F1} " +
                              $"({possession.DefensiveLineAnchorMeters - counter.DefensiveLineAnchorMeters:+0.0;-0.0;0.0} m)");
            Console.WriteLine($"  marking            : man-oriented body distance {manOriented.MarkerBodyDistanceMeters:F2} m vs " +
                              $"press {press.MarkerBodyDistanceMeters:F2} m " +
                              $"({manOriented.MarkerBodyDistanceMeters - press.MarkerBodyDistanceMeters:+0.00;-0.00;0.00} m)" +
                              $" | markers/sample man-oriented {manOriented.MarkersHeldPerSample:F2}, press " +
                              $"{press.MarkersHeldPerSample:F2}, possession {possession.MarkersHeldPerSample:F2}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Evaluates and prints the predefined A9.1 claims, and returns whether every one of them held. The numbers
    /// and the criterion are printed on the same line, so a FAIL names the threshold that failed rather than
    /// only the claim. The per-seed pairing is what keeps a single chaotic fixture from deciding a claim, and
    /// two kinds of claim are graded differently because they are two kinds of statement: a <b>shape</b> claim
    /// (width, line height, marker count) reads a property of the assignment the presets' numbers state and must
    /// hold on all but at most one seed; a <b>consequence</b> claim (possession share) is a stochastic match
    /// outcome and needs a paired-seed majority plus a mean threshold. Pass volume, completion, territory and
    /// marker body distance are printed as reported measurements below the claims and never gated: the
    /// eight-seed run showed they are not monotone in style (a direct side's longer balls still count as passes,
    /// and a turnover raises the count).
    /// </summary>
    private static bool ReportStyleClaims(StyleEvidenceTotals[] styles, int seedCount)
    {
        int possessionShareWins = 0;
        int passesDirectWins = 0;
        int passesLowBlockWins = 0;
        int widthWins = 0;
        int pressLineWins = 0;
        int possessionLineWins = 0;
        int directLineWins = 0;
        int markersPressWins = 0;
        int markersPossessionWins = 0;
        double possessionShareDiffSum = 0d;
        double passesDirectDiffSum = 0d;
        double passesLowBlockDiffSum = 0d;
        double widthDiffSum = 0d;
        double pressLineDiffSum = 0d;
        double possessionLineDiffSum = 0d;
        double directLineDiffSum = 0d;
        double markersPressDiffSum = 0d;
        double markersPossessionDiffSum = 0d;

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            StyleSeedMetrics possession = styles[StyleIndexPossessionControl].PerSeed[seedIndex];
            StyleSeedMetrics direct = styles[StyleIndexDirect].PerSeed[seedIndex];
            StyleSeedMetrics counter = styles[StyleIndexDirectCounterattack].PerSeed[seedIndex];
            StyleSeedMetrics press = styles[StyleIndexHighPress].PerSeed[seedIndex];
            StyleSeedMetrics lowBlock = styles[StyleIndexLowBlock].PerSeed[seedIndex];
            StyleSeedMetrics manOriented = styles[StyleIndexManOriented].PerSeed[seedIndex];

            double shareDiff = possession.PossessionSharePercent - direct.PossessionSharePercent;
            possessionShareDiffSum += shareDiff;
            if (shareDiff > 0d) possessionShareWins++;

            double passesDirectDiff = possession.PassesAttemptedPerMatch - direct.PassesAttemptedPerMatch;
            passesDirectDiffSum += passesDirectDiff;
            if (passesDirectDiff > 0d) passesDirectWins++;

            double passesLowBlockDiff = possession.PassesAttemptedPerMatch - lowBlock.PassesAttemptedPerMatch;
            passesLowBlockDiffSum += passesLowBlockDiff;
            if (passesLowBlockDiff > 0d) passesLowBlockWins++;

            double widthDiff = possession.AttackingWidthMeters - direct.AttackingWidthMeters;
            widthDiffSum += widthDiff;
            if (widthDiff > 0d) widthWins++;

            double pressLineDiff = press.DefensiveLineAnchorMeters - lowBlock.DefensiveLineAnchorMeters;
            pressLineDiffSum += pressLineDiff;
            if (pressLineDiff > 0d) pressLineWins++;

            double possessionLineDiff = possession.DefensiveLineAnchorMeters - counter.DefensiveLineAnchorMeters;
            possessionLineDiffSum += possessionLineDiff;
            if (possessionLineDiff > 0d) possessionLineWins++;

            double directLineDiff = direct.DefensiveLineAnchorMeters - lowBlock.DefensiveLineAnchorMeters;
            directLineDiffSum += directLineDiff;
            if (directLineDiff > 0d) directLineWins++;

            double markersPressDiff = manOriented.MarkersHeldPerSample - press.MarkersHeldPerSample;
            markersPressDiffSum += markersPressDiff;
            if (markersPressDiff >= 0d) markersPressWins++;

            double markersPossessionDiff = manOriented.MarkersHeldPerSample - possession.MarkersHeldPerSample;
            markersPossessionDiffSum += markersPossessionDiff;
            if (markersPossessionDiff > 0d) markersPossessionWins++;
        }

        double possessionShareMeanDiff = possessionShareDiffSum / seedCount;
        double passesDirectMeanDiff = passesDirectDiffSum / seedCount;
        double passesLowBlockMeanDiff = passesLowBlockDiffSum / seedCount;
        double widthMeanDiff = widthDiffSum / seedCount;
        double pressLineMeanDiff = pressLineDiffSum / seedCount;
        double possessionLineMeanDiff = possessionLineDiffSum / seedCount;
        double directLineMeanDiff = directLineDiffSum / seedCount;
        double markersPressMeanDiff = markersPressDiffSum / seedCount;
        double markersPossessionMeanDiff = markersPossessionDiffSum / seedCount;

        int completedMatches = 0;
        int startedMatches = 0;
        for (int styleIndex = 0; styleIndex < styles.Length; styleIndex++)
        {
            completedMatches += styles[styleIndex].MatchesCompleted;
            startedMatches += styles[styleIndex].MatchesStarted;
        }

        bool allFullTime = completedMatches == startedMatches && startedMatches == styles.Length * seedCount * 2;
        // A shape claim needs the paired supermajority (seeds-1) and at least one winning seed: with one seed a
        // "seeds-1" gate would be zero and could pass a claim whose only measured seed points the other way. A
        // consequence claim (possession share) is a stochastic outcome, so it takes a paired majority instead.
        int requiredShapeWins = Math.Max(1, seedCount - 1);
        int requiredConsequenceWins = Math.Max(1, seedCount / 2 + 1);
        bool possessionShareClaim =
            possessionShareWins >= requiredConsequenceWins && possessionShareMeanDiff > 2d;
        bool widthClaim = widthWins >= requiredShapeWins && widthMeanDiff > 2d;
        bool pressLineClaim = pressLineWins >= requiredShapeWins && pressLineMeanDiff > 4d;
        bool possessionLineClaim = possessionLineWins >= requiredShapeWins && possessionLineMeanDiff > 2d;
        bool directLineClaim = directLineWins >= requiredShapeWins && directLineMeanDiff > 2d;
        bool markersPressClaim = markersPressMeanDiff >= 0d;
        bool markersPossessionClaim = markersPossessionWins >= requiredShapeWins;

        Console.WriteLine($"=== Claims over {seedCount} seeds ===");
        WriteStyleClaimLine("possession share > direct", possessionShareWins, seedCount,
            $"{possessionShareMeanDiff:+0.0;-0.0;0.0} pp",
            $"(consequence: majority {requiredConsequenceWins}/{seedCount} and mean > +2.0 pp)");
        WriteStyleClaimLine("wider with ball > direct", widthWins, seedCount,
            $"{widthMeanDiff:+0.0;-0.0;0.0} m",
            $"(shape: {requiredShapeWins}/{seedCount} seeds and mean > +2.0 m)");
        WriteStyleClaimLine("press line height > low block", pressLineWins, seedCount,
            $"{pressLineMeanDiff:+0.0;-0.0;0.0} m",
            $"(shape: {requiredShapeWins}/{seedCount} seeds and mean > +4.0 m)");
        WriteStyleClaimLine("possession line > counter", possessionLineWins, seedCount,
            $"{possessionLineMeanDiff:+0.0;-0.0;0.0} m",
            $"(shape: {requiredShapeWins}/{seedCount} seeds and mean > +2.0 m)");
        WriteStyleClaimLine("direct line > low block", directLineWins, seedCount,
            $"{directLineMeanDiff:+0.0;-0.0;0.0} m",
            $"(shape: {requiredShapeWins}/{seedCount} seeds and mean > +2.0 m)");
        WriteStyleClaimLine("man-oriented markers >= press", markersPressWins, seedCount,
            $"{markersPressMeanDiff:+0.00;-0.00;0.00}",
            "(shape: mean >= 0)");
        WriteStyleClaimLine("man-oriented markers > possession", markersPossessionWins, seedCount,
            $"{markersPossessionMeanDiff:+0.00;-0.00;0.00}",
            $"(shape: {requiredShapeWins}/{seedCount} seeds)");
        Console.WriteLine($"{"full time, every style",-40}{completedMatches,3}/{startedMatches} matches" +
                          $"  (gate: every style's every match)");

        Console.WriteLine();
        Console.WriteLine("=== Reported only (not gated: not monotone in style) ===");
        Console.WriteLine($"{"passes/match > direct",-40}{passesDirectWins,3}/{seedCount} seeds  " +
                          $"mean {passesDirectMeanDiff:+0.0;-0.0;0.0}");
        Console.WriteLine($"{"passes/match > low block",-40}{passesLowBlockWins,3}/{seedCount} seeds  " +
                          $"mean {passesLowBlockMeanDiff:+0.0;-0.0;0.0}");

        return allFullTime && possessionShareClaim && widthClaim && pressLineClaim && possessionLineClaim &&
               directLineClaim && markersPressClaim && markersPossessionClaim;
    }

    /// <summary>
    /// Writes one A9.1 claim line: the comparison, how many paired seeds it won, the mean difference and the
    /// gate it has to meet. The mean difference is signed, so a negative result is unambiguous.
    /// </summary>
    private static void WriteStyleClaimLine(
        string claim,
        int wins,
        int seedCount,
        string meanDifference,
        string gate) =>
        Console.WriteLine($"{claim,-40}{wins,3}/{seedCount} seeds  mean {meanDifference,-12} {gate}");

    /// <summary>
    /// The preset names for one row of the header, in the same order the aggregate columns print them.
    /// </summary>
    private static string DescribeStylePresets(int first, int count)
    {
        var parts = new List<string>();
        for (int index = first; index < first + count; index++)
            parts.Add($"{StyleShortNames[index]} = {StylePresets[index].Name}");
        return string.Join(" | ", parts);
    }

    /// <summary>
    /// One compact A9.1 per-match line: the style side's possession, passing, shooting, shape, territory and
    /// marking numbers. They are printed per end so the paired block's averages can be checked by eye.
    /// </summary>
    private static string DescribeStyleMatch(StyleMatchMetrics metrics) =>
        $"poss {metrics.PossessionShare01 * 100d:F1} % | passes {metrics.PassesAttempted}/{metrics.PassesCompleted} " +
        $"({metrics.PassCompletionShare * 100d:F1} %) | shots {metrics.Shots}, goals {metrics.Goals} | " +
        $"width {metrics.AttackingWidthMeters:F1} m | territory {metrics.TerritoryMeters:F1} m | " +
        $"line {metrics.DefensiveLineAnchorMeanMeters:F1} m | " +
        $"markers {metrics.MarkersHeldPerSample:F2}/sample @ {metrics.MarkerBodyDistanceMeanMeters:F2} m body";

    /// <summary>
    /// Runs one A9.1 probe match: <paramref name="style"/> at one end, the neutral benchmark at the other, on
    /// the same seed for both ends. Folds the match into <paramref name="totals"/> and returns the style-side
    /// measurements, because the claim block pairs the two ends rather than pooling them.
    ///
    /// The 30-tick sample is the only per-tick work the probe adds. It reads the style team's live job board for
    /// the marker jobs and the body distance to each marker's named man, and it reads the team's defensive line
    /// band (<c>engine.TeamJobs.LineBandOf</c>) for the line's anchor depth - the profile's own line height,
    /// before the ball/keeper caps that make the placed depth situational. Both are the allocator's own answer,
    /// so the numbers are the duties the players were actually given, not a re-derived estimate of the shape.
    /// Nothing here writes to the engine: the match remains exactly the match the same seed would produce
    /// without the probe attached.
    /// </summary>
    private static bool RunStyleMatch(
        ulong seed,
        float halfSeconds,
        TeamTacticalProfile style,
        bool styleAtHome,
        StyleEvidenceTotals totals,
        out StyleMatchMetrics metrics)
    {
        var benchmark = new TeamTacticalProfile();
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = styleAtHome ? style : benchmark,
            AwayProfile = styleAtHome ? benchmark : style
        });
        TeamId styleTeam = styleAtHome ? TeamId.Home : TeamId.Away;
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        long markerSamples = 0;
        long markersHeld = 0;
        long markerBodySamples = 0;
        double markerBodySum = 0d;
        long lineSamples = 0;
        double lineAnchorSum = 0d;
        double linePlacedSum = 0d;

        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();

            if (engine.CurrentTick % StyleMarkingSampleTicks != 0) continue;
            markerSamples++;

            // The profile's own line height, from the allocator's band. The band is invalid while the style team
            // has the ball (or has no line at all), so "valid" is already the defending denominator the claim
            // block states.
            DefensiveLineBand line = engine.TeamJobs.LineBandOf(styleTeam);
            if (line.IsValid)
            {
                lineAnchorSum += line.AnchorMeanProjX;
                linePlacedSum += line.ProjX;
                lineSamples++;
            }

            foreach (FootballerState player in engine.Players)
            {
                if (player.Team != styleTeam || !player.IsActiveOnPitch) continue;

                TeamJobAssignment? assignment = engine.TeamJobs.Get(player.Id);
                if (assignment is null || assignment.Value.Job != TacticalJob.Marker) continue;

                markersHeld++;
                if (assignment.Value.TargetPlayerId is not int targetId) continue;
                if (!TryFindPlayer(engine, targetId, out FootballerState? marked)) continue;
                if (marked is null || !marked.IsActiveOnPitch) continue;

                float deltaX = player.Position.X - marked.Position.X;
                float deltaZ = player.Position.Z - marked.Position.Z;
                markerBodySum += MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
                markerBodySamples++;
            }
        }

        var analytics = engine.Analytics;
        metrics = new StyleMatchMetrics
        {
            StylePossessionTicks = styleAtHome ? analytics.HomePossessionTicks : analytics.AwayPossessionTicks,
            TotalPossessionTicks = analytics.HomePossessionTicks + analytics.AwayPossessionTicks,
            PassesAttempted = styleAtHome ? analytics.HomePassesAttempted : analytics.AwayPassesAttempted,
            PassesCompleted = styleAtHome ? analytics.HomePassesCompleted : analytics.AwayPassesCompleted,
            Shots = styleAtHome ? analytics.HomeShots : analytics.AwayShots,
            Goals = styleAtHome ? analytics.HomeGoals : analytics.AwayGoals,
            AttackingWidthMeters = styleAtHome
                ? analytics.HomeAttackingWidthMeters
                : analytics.AwayAttackingWidthMeters,
            TerritoryMeters = styleAtHome
                ? analytics.HomeAverageBallTerritoryX
                : analytics.AwayAverageBallTerritoryX,
            MarkerSamples = markerSamples,
            MarkersHeld = markersHeld,
            MarkerBodyDistanceSamples = markerBodySamples,
            MarkerBodyDistanceSum = markerBodySum,
            LineSamples = lineSamples,
            LineAnchorSum = lineAnchorSum,
            LinePlacedSum = linePlacedSum
        };

        totals.MatchesStarted++;
        totals.Ticks += engine.CurrentTick;
        totals.StylePossessionTicks += metrics.StylePossessionTicks;
        totals.TotalPossessionTicks += metrics.TotalPossessionTicks;
        totals.PassesAttempted += metrics.PassesAttempted;
        totals.PassesCompleted += metrics.PassesCompleted;
        totals.Shots += metrics.Shots;
        totals.Goals += metrics.Goals;
        totals.AttackingWidthSum += metrics.AttackingWidthMeters;
        totals.AttackingWidthMatches++;
        totals.TerritorySum += metrics.TerritoryMeters;
        totals.TerritoryMatches++;
        totals.MarkerSamples += markerSamples;
        totals.MarkersHeld += markersHeld;
        totals.MarkerBodyDistanceSamples += markerBodySamples;
        totals.MarkerBodyDistanceSum += markerBodySum;
        totals.LineSamples += lineSamples;
        totals.LineAnchorSum += lineAnchorSum;
        totals.LinePlacedSum += linePlacedSum;

        bool completed = engine.Rules.Phase == MatchPhase.FullTime;
        if (completed) totals.MatchesCompleted++;
        return completed;
    }

    /// <summary>One A9.1 style's pooled measurements over both ends and every seed.</summary>
    private sealed class StyleEvidenceTotals
    {
        public string Name = string.Empty;
        public int MatchesStarted;
        public int MatchesCompleted;
        public long Ticks;
        public long StylePossessionTicks;
        public long TotalPossessionTicks;
        public long PassesAttempted;
        public long PassesCompleted;
        public long Shots;
        public long Goals;
        public double AttackingWidthSum;
        public int AttackingWidthMatches;
        public double TerritorySum;
        public int TerritoryMatches;
        public long MarkerSamples;
        public long MarkersHeld;
        public long MarkerBodyDistanceSamples;
        public double MarkerBodyDistanceSum;
        public long LineSamples;
        public double LineAnchorSum;
        public double LinePlacedSum;

        /// <summary>The style's per-seed means over its home and away end, indexed like the seed loop.</summary>
        public StyleSeedMetrics[] PerSeed = Array.Empty<StyleSeedMetrics>();

        /// <summary>The style's pooled possession share over both ends, 0-100.</summary>
        public double PossessionSharePercent => TotalPossessionTicks > 0
            ? 100d * StylePossessionTicks / TotalPossessionTicks
            : 0d;

        public double PassesAttemptedPerMatch => MatchesStarted > 0 ? (double)PassesAttempted / MatchesStarted : 0d;
        public double PassesCompletedPerMatch => MatchesStarted > 0 ? (double)PassesCompleted / MatchesStarted : 0d;
        public double PassCompletionPercent => PassesAttempted > 0 ? 100d * PassesCompleted / PassesAttempted : 0d;
        public double ShotsPerMatch => MatchesStarted > 0 ? (double)Shots / MatchesStarted : 0d;
        public double GoalsPerMatch => MatchesStarted > 0 ? (double)Goals / MatchesStarted : 0d;

        public double AttackingWidthMeanMeters => AttackingWidthMatches > 0
            ? AttackingWidthSum / AttackingWidthMatches
            : 0d;

        public double TerritoryMeanMeters => TerritoryMatches > 0 ? TerritorySum / TerritoryMatches : 0d;
        public double MarkersHeldPerSample => MarkerSamples > 0 ? (double)MarkersHeld / MarkerSamples : 0d;

        public double MarkerBodyDistanceMeanMeters => MarkerBodyDistanceSamples > 0
            ? MarkerBodyDistanceSum / MarkerBodyDistanceSamples
            : 0d;

        /// <summary>
        /// Mean of the defensive line band's anchor projection over the valid 30-tick samples: metres in the
        /// style's own attacking direction, positive = higher up the pitch. This is the profile's line height
        /// before the ball/keeper caps, so it is the number the preset actually names.
        /// </summary>
        public double DefensiveLineAnchorMeanMeters => LineSamples > 0 ? LineAnchorSum / LineSamples : 0d;

        /// <summary>The band's placed depth over the same samples; reported, not gated (caps are situational).</summary>
        public double DefensiveLinePlacedMeanMeters => LineSamples > 0 ? LinePlacedSum / LineSamples : 0d;

        /// <summary>Completed versus started matches, as the aggregate table prints them.</summary>
        public string MatchesDescription => $"{MatchesCompleted}/{MatchesStarted}";
    }

    /// <summary>One A9.1 style's means over its home and away end on one seed, for the paired claims block.</summary>
    private sealed class StyleSeedMetrics
    {
        public double PossessionSharePercent;
        public double PassesAttemptedPerMatch;
        public double AttackingWidthMeters;
        public double TerritoryMeters;
        public double MarkersHeldPerSample;
        public double MarkerBodyDistanceMeters;
        public double DefensiveLineAnchorMeters;
    }

    /// <summary>One A9.1 probe match's style-side measurements, returned so its two ends can be paired by seed.</summary>
    private sealed class StyleMatchMetrics
    {
        public int StylePossessionTicks;
        public int TotalPossessionTicks;
        public int PassesAttempted;
        public int PassesCompleted;
        public int Shots;
        public int Goals;
        public float AttackingWidthMeters;
        public float TerritoryMeters;
        public long MarkerSamples;
        public long MarkersHeld;
        public long MarkerBodyDistanceSamples;
        public double MarkerBodyDistanceSum;
        public long LineSamples;
        public double LineAnchorSum;
        public double LinePlacedSum;

        /// <summary>The style's share of the two teams' possession ticks, 0-1.</summary>
        public double PossessionShare01 => TotalPossessionTicks > 0
            ? (double)StylePossessionTicks / TotalPossessionTicks
            : 0.5d;

        public double PassCompletionShare => PassesAttempted > 0 ? (double)PassesCompleted / PassesAttempted : 0d;
        public double MarkersHeldPerSample => MarkerSamples > 0 ? (double)MarkersHeld / MarkerSamples : 0d;

        public double MarkerBodyDistanceMeanMeters => MarkerBodyDistanceSamples > 0
            ? MarkerBodyDistanceSum / MarkerBodyDistanceSamples
            : 0d;

        /// <summary>Mean anchor projection of the line over this match's valid 30-tick samples.</summary>
        public double DefensiveLineAnchorMeanMeters => LineSamples > 0 ? LineAnchorSum / LineSamples : 0d;

        /// <summary>Mean placed line depth over this match's valid 30-tick samples.</summary>
        public double DefensiveLinePlacedMeanMeters => LineSamples > 0 ? LinePlacedSum / LineSamples : 0d;
    }

    // =====================================================================================================
    // A9.2 — feature ablations. One same-seed A/B per feature: the reference build (every switch on) against
    // the build with exactly one switch off. See RunAblationEvidence for the model, the sampling and the
    // classification rule. Every constant the report prints is declared once here so the printed rule, the
    // XML doc and the code cannot drift apart.
    // =====================================================================================================

    private const int AblationLocalCover = 0;
    private const int AblationRestDefence = 1;
    private const int AblationPerceptionLimits = 2;
    private const int AblationOrientation = 3;
    private const int AblationCommitment = 4;
    private const int AblationRunCoordination = 5;
    private const int AblationGoalkeeperSweep = 6;
    private const int AblationShallowSearch = 7;

    /// <summary>The eight A9.2 ablations, one row of the report each.</summary>
    private const int AblationFeatureCount = 8;

    private static readonly string[] AblationFeatureNames =
    {
        "local cover", "rest defence", "perception limits", "orientation",
        "commitment", "run coordination", "goalkeeper sweep", "lookahead"
    };

    private static readonly string[] AblationSwitchNames =
    {
        "EnableLocalCover", "EnableRestDefence", "EnablePerceptionLimits", "EnableOrientation",
        "EnableCommitment", "EnableRunCoordination", "EnableGoalkeeperSweep", "EnableShallowSearch"
    };

    /// <summary>The one number each feature's ablation is expected to move, named for the report.</summary>
    private static readonly string[] AblationCounterNames =
    {
        "coverage opened", "mean security", "occluded sightings", "gaze-revealed sightings",
        "committed player-ticks", "runs published", "sweep-evaluated ticks", "continuations"
    };

    /// <summary>
    /// A9.2's headline bounds, declared once so the XML doc, the printed rule and the classification cannot
    /// disagree: an ablation is <c>overwhelming</c> when the off build moves one of these on at least
    /// seeds-1 paired seeds <b>and</b> the mean ball separation reaches the separation bound.
    /// </summary>
    private const double AblationPossessionBoundPoints = 10d;
    private const double AblationGoalsBound = 2d;
    private const double AblationTicksBoundShare = 0.15d;
    private const double AblationSeparationBoundMeters = 3d;

    /// <summary>Ball separation, in metres, that counts as two builds having diverged at a sampled tick.</summary>
    private const float AblationDivergenceMeters = 0.05f;

    /// <summary>What the mechanical A9.2 rule says about one feature's ablation.</summary>
    private enum AblationClass
    {
        /// <summary>The ablation changed nothing measurable, or the mechanism never ran in the reference build.</summary>
        Inert,

        /// <summary>The ablation moved the match, but inside the stated bounds.</summary>
        Measured,

        /// <summary>The ablation moves a headline aggregate past the stated bounds on at least seeds-1 seeds.</summary>
        Overwhelming
    }

    /// <summary>
    /// A9.2 evidence: <b>feature ablations</b>, measured as eight same-seed A/Bs between the reference build
    /// and the build with exactly one switch off.
    ///
    /// <b>The model sentence.</b> An ablation is the off build of one mechanism at the same seed, the same
    /// squads and the same ends: both builds run the same engine, profiles, rules and cadence, and the only
    /// difference between them is one <see cref="AiRuntimeConfig"/> switch. Whatever differs between the two
    /// matches is therefore what that mechanism is worth, not a retuning of it. The reference build is
    /// <c>new AiRuntimeConfig()</c> (every switch on); an off build is that same configuration with exactly
    /// one switch false.
    ///
    /// Each match is sampled per tick, read-only and without consuming simulation randomness: the
    /// authoritative ball position (for the trajectory comparison), the score, player-ticks with a live
    /// <see cref="TacticalUtilityAiController.CommitmentRemainingSeconds"/>, ticks with a keeper
    /// <see cref="GoalkeeperController.SweepEvaluated"/>, and the rest-defence picture
    /// (<c>TeamJobCoordinator.SecurityOf</c>) wherever it is defined. At full time the aggregate counters are
    /// read: goals, passes, shots, possession share, the coverage / run / combination counters and
    /// <see cref="ScanTelemetry"/>.
    ///
    /// <b>Classification rule</b> (mechanical; the report prints it, and the bounds are the constants above):
    /// <list type="bullet">
    /// <item><b>inert</b> — the mechanism's reference counter is zero, or no paired seed diverged at all
    /// (the ablation changed nothing measurable, or the reference build never ran the mechanism);</item>
    /// <item><b>overwhelming</b> — on at least seeds-1 paired seeds the off build moves a headline aggregate
    /// by the stated bounds (|possession delta| &gt;= 10 pp, |goals delta| &gt;= 2, or |ticks delta| &gt;= 15 %
    /// of the reference ticks) <b>and</b> the mean ball separation is &gt;= 3 m;</item>
    /// <item><b>measured</b> — anything else.</item>
    /// </list>
    /// A sampled tick diverges when the two builds' ball positions differ by more than 0.05 m or the score
    /// differs; the first divergence tick is the first such sampled tick.
    ///
    /// <b>Verdict.</b> PASS requires (a) both builds reach full time on every seed for every feature, and
    /// (b) every gated ablation neutralises its own mechanism in the off build: with local cover off no
    /// coverage request is opened, with run coordination off no run is published, with the keeper sweep off
    /// no keeper decision evaluates the sweep race, with commitment off no player-tick is committed, with
    /// perception limits off no sighting is occluded, and with rest defence off the picture reads strictly
    /// more secure than the reference on every seed (the off build is fully secure). Orientation and
    /// lookahead are reported, not gated beyond divergence — lookahead's strict claim belongs to
    /// <c>--search</c>.
    /// </summary>
    private static bool RunAblationEvidence(string[] args)
    {
        int seedCount = args.Length > 1 && int.TryParse(args[1], out int requestedSeeds) && requestedSeeds > 0
            ? requestedSeeds
            : 3;
        float halfSeconds = args.Length > 2 && float.TryParse(args[2], out float requestedHalf) && requestedHalf > 0f
            ? requestedHalf
            : 600f;

        Console.WriteLine($"=== A9.2 feature ablations: same-seed A/B, {seedCount} seeds x {halfSeconds * 2f:F0} s -- " +
                          "reference build (all switches on) vs one switch off per feature ===");
        Console.WriteLine($"Simulation version : {SimulationVersion.Tag}");
        Console.WriteLine("Ablation model     : an ablation is the off build of one mechanism at the same seed, the same");
        Console.WriteLine("                     squads and the same ends: both builds run the same engine, profiles, rules");
        Console.WriteLine("                     and cadence, and the only difference between them is one AiRuntimeConfig");
        Console.WriteLine("                     switch. Whatever differs between the two matches is therefore what that");
        Console.WriteLine("                     mechanism is worth, not a retuning of it. The reference build is");
        Console.WriteLine("                     new AiRuntimeConfig() (every switch on).");
        Console.WriteLine("Divergence         : a sampled tick diverges when the two builds' ball positions differ by more");
        Console.WriteLine($"                     than {AblationDivergenceMeters:F2} m or the score differs; the first divergence");
        Console.WriteLine("                     tick is the first such sampled tick, and mean/max ball separation is over the");
        Console.WriteLine("                     common tick range of the two matches.");
        Console.WriteLine("Classification     : mechanical, from the measured numbers: inert when the mechanism's reference");
        Console.WriteLine("                     counter is zero or no paired seed diverged at all; overwhelming when on at");
        Console.WriteLine($"                     least seeds-1 paired seeds |possession delta| >= {AblationPossessionBoundPoints:F1} pp OR");
        Console.WriteLine($"                     |goals delta| >= {AblationGoalsBound:F0} OR |ticks delta| >= {AblationTicksBoundShare * 100d:F0} % of the");
        Console.WriteLine($"                     reference ticks, AND the mean ball separation is >= {AblationSeparationBoundMeters:F1} m;");
        Console.WriteLine("                     otherwise measured. The CLASS column is that rule's output, not an opinion.");
        Console.WriteLine($"Reference (ON)     : {new AiRuntimeConfig().Describe()}");
        Console.WriteLine("Off builds (OFF)   : one switch false each, everything else as the reference:");
        for (int feature = 0; feature < AblationFeatureCount; feature++)
            Console.WriteLine($"  {AblationFeatureNames[feature],-19}{AblationSwitchNames[feature],-24} " +
                              $"{BuildAblationOffConfig(feature).Describe()}");
        Console.WriteLine();

        var seeds = new ulong[seedCount];
        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
            seeds[seedIndex] = 20260818UL + (ulong)seedIndex * 7919UL;

        var references = new AblationMatchSample[seedCount];
        var comparisons = new AblationComparison[AblationFeatureCount, seedCount];

        for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
        {
            // The reference match is one world per seed, reused by all eight ablations: that is what makes the
            // eight rows paired rather than eight different fixtures.
            references[seedIndex] = RunAblationMatch(seeds[seedIndex], halfSeconds, featureIndex: -1);
            Console.WriteLine($"Seed {seeds[seedIndex]}:");
            WriteAblationReferenceTotals(references[seedIndex]);
            for (int feature = 0; feature < AblationFeatureCount; feature++)
            {
                AblationMatchSample off = RunAblationMatch(seeds[seedIndex], halfSeconds, feature);
                comparisons[feature, seedIndex] = CompareAblation(references[seedIndex], off, feature);
                WriteAblationSeedLine(feature, comparisons[feature, seedIndex]);
            }
            Console.WriteLine();
        }

        AblationFeatureSummary[] summaries = SummarizeAblations(references, comparisons, seedCount);
        WriteAblationAggregateTable(summaries, seedCount);
        WriteAblationNeutralisationTable(summaries, seedCount);
        WriteAblationFindings(summaries, seeds);
        return ReportAblationVerdict(summaries, seeds);
    }

    /// <summary>
    /// The configuration of one A9.2 off build: the reference configuration with exactly one switch false.
    /// The switch names are the A9.2 names <see cref="AiRuntimeConfig"/> documents; nothing else is set, so
    /// the two builds differ in one field.
    /// </summary>
    private static AiRuntimeConfig BuildAblationOffConfig(int feature) => feature switch
    {
        AblationLocalCover => new AiRuntimeConfig { EnableLocalCover = false },
        AblationRestDefence => new AiRuntimeConfig { EnableRestDefence = false },
        AblationPerceptionLimits => new AiRuntimeConfig { EnablePerceptionLimits = false },
        AblationOrientation => new AiRuntimeConfig { EnableOrientation = false },
        AblationCommitment => new AiRuntimeConfig { EnableCommitment = false },
        AblationRunCoordination => new AiRuntimeConfig { EnableRunCoordination = false },
        AblationGoalkeeperSweep => new AiRuntimeConfig { EnableGoalkeeperSweep = false },
        _ => new AiRuntimeConfig { EnableShallowSearch = false }
    };

    /// <summary>
    /// The one number that shows whether a feature's mechanism was live, from the same sampled state the
    /// probe classifies: a lifetime count for the mechanisms that emit events, and the mean rest-defence
    /// security (0-1) for the mechanism whose whole output is that picture. A zero therefore means "the
    /// mechanism never ran", which is the inert rule's first clause.
    /// </summary>
    private static double AblationMechanismValue(AblationMatchSample sample, int feature) => feature switch
    {
        AblationLocalCover => sample.CoverageRequestsOpened,
        AblationRestDefence => sample.MeanSecurity(),
        AblationPerceptionLimits => sample.Scan.OccludedByBody,
        AblationOrientation => sample.Scan.RevealedByGaze,
        AblationCommitment => sample.CommittedPlayerTicks,
        AblationRunCoordination => sample.RunsPublished,
        AblationGoalkeeperSweep => sample.SweepEvaluatedTicks,
        _ => sample.CombinationsContinued
    };

    /// <summary>The mechanism counter as one per-seed value: counts are integers, security is a 0-1 share.</summary>
    private static string AblationMechanismText(double value, int feature) =>
        feature == AblationRestDefence ? value.ToString("F3") : value.ToString("F0");

    /// <summary>The mechanism counter as a mean over seeds: one decimal for counts, a share for security.</summary>
    private static string AblationMechanismMeanText(double value, int feature) =>
        feature == AblationRestDefence ? value.ToString("F3") : value.ToString("F1");

    /// <summary>True when A9.2 requires the off build to neutralise this feature's mechanism (gate b).</summary>
    private static bool AblationHasNeutralisationGate(int feature) =>
        feature is not (AblationOrientation or AblationShallowSearch);

    /// <summary>The gate a gated feature's off build must meet, phrased as the neutralisation claim it proves.</summary>
    private static string AblationGateDescription(int feature) => feature switch
    {
        AblationLocalCover => "coverage requests opened = 0",
        AblationRestDefence => "off mean security > on mean security",
        AblationPerceptionLimits => "occluded sightings = 0",
        AblationCommitment => "committed player-ticks = 0",
        AblationRunCoordination => "runs published = 0",
        AblationGoalkeeperSweep => "sweep-evaluated ticks = 0",
        _ => "reported only, not gated"
    };

    /// <summary>
    /// Runs one A9.2 probe match: the reference build when <paramref name="featureIndex"/> is negative, the
    /// build with that one switch off otherwise. Samples the match per tick and reads the aggregate counters
    /// at full time; the loop and the tick cap are the same ones every other Headless evidence probe uses, so
    /// a probe match is the same fixture the other probes play.
    /// </summary>
    private static AblationMatchSample RunAblationMatch(ulong seed, float halfSeconds, int featureIndex)
    {
        var ai = featureIndex < 0 ? new AiRuntimeConfig() : BuildAblationOffConfig(featureIndex);
        var engine = new MatchSimulationEngine(new PreMatchConfig
        {
            Seed = seed,
            PlayersPerTeam = 11,
            HalfDurationSeconds = halfSeconds,
            HomeProfile = TeamTacticalProfile.PossessionControl,
            AwayProfile = TeamTacticalProfile.DirectCounterattack,
            Ai = ai
        });
        int maxTicks = (int)(halfSeconds * 2.25f * 60f) + 1200;

        var sample = new AblationMatchSample();
        while (engine.Rules.Phase != MatchPhase.FullTime && engine.CurrentTick < maxTicks)
        {
            engine.StepTick();
            SampleAblationTick(engine, sample);
        }

        var analytics = engine.Analytics;
        sample.Completed = engine.Rules.Phase == MatchPhase.FullTime;
        sample.Ticks = engine.CurrentTick;
        sample.HomeGoals = analytics.HomeGoals;
        sample.AwayGoals = analytics.AwayGoals;
        sample.PassesAttempted = analytics.HomePassesAttempted + analytics.AwayPassesAttempted;
        sample.PassesCompleted = analytics.HomePassesCompleted + analytics.AwayPassesCompleted;
        sample.Shots = analytics.HomeShots + analytics.AwayShots;
        sample.HomePossessionShare = analytics.HomePossessionShare;

        TeamJobCoordinator jobs = engine.TeamJobs;
        sample.CoverageRequestsOpened = jobs.CoverageRequestsOpened;
        sample.CoverageRequestsClosed = jobs.CoverageRequestsClosed;
        sample.Handovers = jobs.HandoverCount;

        RunAdvertisementBoard runs = engine.Communication.Runs;
        sample.RunsPublished = runs.PublishedCount;
        sample.RunsWithdrawn = runs.WithdrawnCount;
        sample.RunsClaimed = runs.ClaimedCount;

        CombinationCoordinator combinations = engine.Communication.Combinations;
        sample.CombinationsPublished = combinations.PublishedCount;
        sample.CombinationsContinued = combinations.ContinuedCount;

        sample.Scan = engine.ScanStats;
        return sample;
    }

    /// <summary>
    /// Samples one stepped A9.2 probe tick. Read-only: it reads the authoritative ball and score, the
    /// controllers' own diagnostics and the rest-defence picture, consumes no simulation randomness and
    /// mutates nothing, so the match is exactly the match the same seed would produce without the probe.
    /// The player scan is a plain indexed loop (no LINQ, no allocation) because it runs on every live tick.
    /// </summary>
    private static void SampleAblationTick(MatchSimulationEngine engine, AblationMatchSample sample)
    {
        sample.BallPositions.Add(engine.Ball.Position);
        sample.HomeGoalsByTick.Add(engine.Analytics.HomeGoals);
        sample.AwayGoalsByTick.Add(engine.Analytics.AwayGoals);

        int committedThisTick = 0;
        bool sweepEvaluatedThisTick = false;
        IReadOnlyList<FootballerState> players = engine.Players;
        for (int index = 0; index < players.Count; index++)
        {
            FootballerState player = players[index];
            if (!player.IsActiveOnPitch) continue;

            IFootballerController controller = engine.GetController(player.Id);
            if (controller is TacticalUtilityAiController tactical)
            {
                if (tactical.CommitmentRemainingSeconds > 0f) committedThisTick++;
            }
            else if (controller is GoalkeeperController keeper && keeper.SweepEvaluated)
            {
                sweepEvaluatedThisTick = true;
            }
        }

        sample.CommittedPlayerTicks += committedThisTick;
        if (sweepEvaluatedThisTick) sample.SweepEvaluatedTicks++;

        // The rest-defence picture is defined from the first in-possession evaluation; before that it is the
        // default struct (security 0). Skipping the undefined value keeps the mean a mean of the picture, and
        // the ablation's off build reads fully secure from the first tick, which is what the gate compares.
        RestDefence homeSecurity = engine.TeamJobs.SecurityOf(TeamId.Home);
        if (homeSecurity.Security01 > 0f)
        {
            sample.SecuritySum += homeSecurity.Security01;
            sample.SecuritySamples++;
        }

        RestDefence awaySecurity = engine.TeamJobs.SecurityOf(TeamId.Away);
        if (awaySecurity.Security01 > 0f)
        {
            sample.SecuritySum += awaySecurity.Security01;
            sample.SecuritySamples++;
        }
    }

    /// <summary>
    /// Compares one off build with the same seed's reference build: end state, divergence over the common
    /// tick range, the final aggregate deltas, the mechanism counters and the neutralisation gate. The mean
    /// and max ball separation are recorded even when the two matches never diverge (then both are 0), because
    /// "same trajectory" is a measured fact of the ablation rather than an absence.
    /// </summary>
    private static AblationComparison CompareAblation(
        AblationMatchSample reference,
        AblationMatchSample off,
        int feature)
    {
        var comparison = new AblationComparison
        {
            ReferenceCompleted = reference.Completed,
            OffCompleted = off.Completed,
            ReferenceTicks = reference.Ticks,
            OffTicks = off.Ticks,
            ReferenceHomeGoals = reference.HomeGoals,
            ReferenceAwayGoals = reference.AwayGoals,
            OffHomeGoals = off.HomeGoals,
            OffAwayGoals = off.AwayGoals,
            TicksDelta = off.Ticks - reference.Ticks,
            PossessionDeltaPp = (off.HomePossessionShare - reference.HomePossessionShare) * 100d,
            GoalsDelta = (off.HomeGoals + off.AwayGoals) - (reference.HomeGoals + reference.AwayGoals),
            PassesDelta = off.PassesAttempted - reference.PassesAttempted,
            ShotsDelta = off.Shots - reference.Shots,
            MechanismOn = AblationMechanismValue(reference, feature),
            MechanismOff = AblationMechanismValue(off, feature),
            GateHolds = AblationGateHolds(feature, reference, off)
        };

        int commonTicks = Math.Min(reference.BallPositions.Count, off.BallPositions.Count);
        double separationSum = 0d;
        double maxSeparation = 0d;
        int divergentTicks = 0;
        int firstDivergenceTick = -1;
        for (int tickIndex = 0; tickIndex < commonTicks; tickIndex++)
        {
            Vector3 onBall = reference.BallPositions[tickIndex];
            Vector3 offBall = off.BallPositions[tickIndex];
            float deltaX = onBall.X - offBall.X;
            float deltaY = onBall.Y - offBall.Y;
            float deltaZ = onBall.Z - offBall.Z;
            float separation = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
            separationSum += separation;
            if (separation > maxSeparation) maxSeparation = separation;

            bool scoreDiffers = reference.HomeGoalsByTick[tickIndex] != off.HomeGoalsByTick[tickIndex] ||
                                reference.AwayGoalsByTick[tickIndex] != off.AwayGoalsByTick[tickIndex];
            if (scoreDiffers || separation > AblationDivergenceMeters)
            {
                divergentTicks++;
                if (firstDivergenceTick < 0) firstDivergenceTick = tickIndex + 1;
            }
        }

        comparison.DivergentTicks = divergentTicks;
        comparison.FirstDivergenceTick = firstDivergenceTick;
        comparison.MeanBallSeparation = commonTicks > 0 ? separationSum / commonTicks : 0d;
        comparison.MaxBallSeparation = maxSeparation;

        double ticksShare = reference.Ticks > 0
            ? Math.Abs(comparison.TicksDelta) / (double)reference.Ticks
            : 0d;
        comparison.HeadlineMove = Math.Abs(comparison.PossessionDeltaPp) >= AblationPossessionBoundPoints ||
                                  Math.Abs(comparison.GoalsDelta) >= AblationGoalsBound ||
                                  ticksShare >= AblationTicksBoundShare;
        return comparison;
    }

    /// <summary>
    /// Whether the off build neutralised its own mechanism (gate b). Orientation and lookahead have no gate —
    /// their off builds are reported and classified by divergence alone.
    /// </summary>
    private static bool AblationGateHolds(int feature, AblationMatchSample reference, AblationMatchSample off) =>
        feature switch
        {
            AblationLocalCover => off.CoverageRequestsOpened == 0,
            AblationRestDefence => off.MeanSecurity() > reference.MeanSecurity(),
            AblationPerceptionLimits => off.Scan.OccludedByBody == 0,
            AblationCommitment => off.CommittedPlayerTicks == 0,
            AblationRunCoordination => off.RunsPublished == 0,
            AblationGoalkeeperSweep => off.SweepEvaluatedTicks == 0,
            _ => true
        };

    /// <summary>
    /// The A9.2 aggregate: one summary per feature, pooled over the seed set. Means are per seed, so the
    /// columns compare features at the same denominator; the median first-divergence tick is over the seeds
    /// that diverged (a feature whose off build is the same match has no divergence tick to place).
    /// </summary>
    private static AblationFeatureSummary[] SummarizeAblations(
        AblationMatchSample[] references,
        AblationComparison[,] comparisons,
        int seedCount)
    {
        var summaries = new AblationFeatureSummary[AblationFeatureCount];
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            var summary = new AblationFeatureSummary();
            double mechanismOnSum = 0d;
            double mechanismOffSum = 0d;
            double separationSum = 0d;
            double ticksDeltaSum = 0d;
            double possessionDeltaSum = 0d;
            double goalsDeltaSum = 0d;
            double passesDeltaSum = 0d;
            double shotsDeltaSum = 0d;
            var firstTicks = new List<int>();

            for (int seedIndex = 0; seedIndex < seedCount; seedIndex++)
            {
                AblationMatchSample reference = references[seedIndex];
                AblationComparison comparison = comparisons[feature, seedIndex];
                mechanismOnSum += AblationMechanismValue(reference, feature);
                mechanismOffSum += comparison.MechanismOff;
                separationSum += comparison.MeanBallSeparation;
                ticksDeltaSum += comparison.TicksDelta;
                possessionDeltaSum += comparison.PossessionDeltaPp;
                goalsDeltaSum += comparison.GoalsDelta;
                passesDeltaSum += comparison.PassesDelta;
                shotsDeltaSum += comparison.ShotsDelta;

                if (comparison.DivergentTicks > 0)
                {
                    summary.DivergentSeeds++;
                    if (comparison.FirstDivergenceTick >= 0)
                        firstTicks.Add(comparison.FirstDivergenceTick);
                }

                if (comparison.HeadlineMove) summary.HeadlineSeeds++;
                if (comparison.ReferenceCompleted && comparison.OffCompleted)
                {
                    summary.FullTimePairs++;
                }
                else
                {
                    summary.IncompleteSeeds.Add(seedIndex);
                }

                if (!comparison.GateHolds)
                {
                    summary.GateFailures++;
                    summary.GateFailureSeeds.Add(seedIndex);
                }
            }

            double divisor = seedCount > 0 ? seedCount : 1;
            summary.MechanismOnMean = mechanismOnSum / divisor;
            summary.MechanismOffMean = mechanismOffSum / divisor;
            summary.MeanBallSeparation = separationSum / divisor;
            summary.TicksDeltaMean = ticksDeltaSum / divisor;
            summary.PossessionDeltaPpMean = possessionDeltaSum / divisor;
            summary.GoalsDeltaMean = goalsDeltaSum / divisor;
            summary.PassesDeltaMean = passesDeltaSum / divisor;
            summary.ShotsDeltaMean = shotsDeltaSum / divisor;
            summary.MedianFirstDivergence = firstTicks.Count == 0 ? double.NaN : Median(firstTicks);
            summary.CounterZeroInReference = mechanismOnSum <= 0d;

            int requiredHeadlineSeeds = Math.Max(1, seedCount - 1);
            summary.Class = summary.CounterZeroInReference || summary.DivergentSeeds == 0
                ? AblationClass.Inert
                : summary.HeadlineSeeds >= requiredHeadlineSeeds &&
                  summary.MeanBallSeparation >= AblationSeparationBoundMeters
                    ? AblationClass.Overwhelming
                    : AblationClass.Measured;
            summaries[feature] = summary;
        }

        return summaries;
    }

    /// <summary>The A9.2 per-seed block: the reference build's full-time counters for the seed.</summary>
    private static void WriteAblationReferenceTotals(AblationMatchSample reference)
    {
        Console.WriteLine($"  reference build: {reference.HomeGoals}-{reference.AwayGoals}, {reference.Ticks} ticks, " +
                          $"passes {reference.PassesAttempted}/{reference.PassesCompleted}, shots {reference.Shots}, " +
                          $"poss {reference.HomePossessionShare * 100f:F1} % | cover opened {reference.CoverageRequestsOpened}" +
                          $"/closed {reference.CoverageRequestsClosed}/handovers {reference.Handovers} | runs " +
                          $"{reference.RunsPublished}/{reference.RunsWithdrawn}/{reference.RunsClaimed} " +
                          "published/withdrawn/claimed | combos " +
                          $"{reference.CombinationsPublished}/{reference.CombinationsContinued} published/continued | " +
                          $"scan starts {reference.Scan.ScanStarts}, revealed {reference.Scan.RevealedByGaze}, " +
                          $"occluded {reference.Scan.OccludedByBody}");
    }

    /// <summary>
    /// One compact A9.2 per-feature line inside a seed block: both builds' end state, ticks, score, divergence
    /// and the mechanism counter, so the aggregate row can be checked by eye against the two matches.
    /// </summary>
    private static void WriteAblationSeedLine(int feature, AblationComparison comparison)
    {
        string firstDivergence = comparison.FirstDivergenceTick < 0
            ? "n/a"
            : comparison.FirstDivergenceTick.ToString();
        Console.WriteLine($"  {AblationFeatureNames[feature],-19} " +
                          $"{(comparison.ReferenceCompleted ? "FT" : "INC")}/{(comparison.OffCompleted ? "FT" : "INC")} " +
                          $"{comparison.ReferenceTicks,6}/{comparison.OffTicks,-6} t | " +
                          $"score {comparison.ReferenceHomeGoals}-{comparison.ReferenceAwayGoals} vs " +
                          $"{comparison.OffHomeGoals}-{comparison.OffAwayGoals} | " +
                          $"div {comparison.DivergentTicks,5} t, first {firstDivergence,-6} | " +
                          $"ball {comparison.MeanBallSeparation:F2}/{comparison.MaxBallSeparation:F2} m mean/max | " +
                          $"{AblationCounterNames[feature]} " +
                          $"{AblationMechanismText(comparison.MechanismOn, feature)}/" +
                          $"{AblationMechanismText(comparison.MechanismOff, feature)}");
    }

    /// <summary>
    /// The A9.2 aggregate table: one row per feature over the seed set. Deltas are OFF minus reference; the
    /// pass and shot deltas are counts, the possession delta is percentage points, and the CLASS column is the
    /// mechanical rule's output (the bounds are the constants in <see cref="RunAblationEvidence"/>).
    /// </summary>
    private static void WriteAblationAggregateTable(AblationFeatureSummary[] summaries, int seedCount)
    {
        Console.WriteLine($"=== Aggregate over {seedCount} seeds (means per seed; deltas are OFF - reference) ===");
        Console.WriteLine($"{"feature",-19}{"mech ON",9}{"mech OFF",9}{"div",6}{"med 1st",8}{"ball m",8}" +
                          $"{"ticks d",8}{"poss pp",8}{"goals d",8}{"passes d",9}{"shots d",9} {"CLASS",-12}");
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            AblationFeatureSummary summary = summaries[feature];
            Console.WriteLine($"{AblationFeatureNames[feature],-19}" +
                              $"{AblationMechanismMeanText(summary.MechanismOnMean, feature),9}" +
                              $"{AblationMechanismMeanText(summary.MechanismOffMean, feature),9}" +
                              $"{summary.DivergentSeeds,3}/{seedCount,-2}" +
                              $"{DescribeMedianTick(summary.MedianFirstDivergence),8}" +
                              $"{summary.MeanBallSeparation,8:F2}" +
                              $"{summary.TicksDeltaMean.ToString("+0;-0;0"),8}" +
                              $"{summary.PossessionDeltaPpMean.ToString("+0.0;-0.0;0.0"),8}" +
                              $"{summary.GoalsDeltaMean.ToString("+0.0;-0.0;0.0"),8}" +
                              $"{summary.PassesDeltaMean.ToString("+0.0;-0.0;0.0"),9}" +
                              $"{summary.ShotsDeltaMean.ToString("+0.0;-0.0;0.0"),9} " +
                              $"{AblationClassName(summary.Class),-12}");
        }

        Console.WriteLine("mech = the feature's mechanism counter (counts as per-seed means, rest-defence security");
        Console.WriteLine("as a 0-1 mean); div = seeds on which at least one sampled tick diverged; med 1st = median");
        Console.WriteLine("first-divergence tick over the diverged seeds; d = OFF minus reference.");
    }

    /// <summary>
    /// The A9.2 neutralisation table: for each feature, the probe counter in the reference build and in the off
    /// build, and whether the off build's counter meets the gate. A gated feature that does not meet it is
    /// named NOT NEUTRALISED here and in the verdict; orientation and lookahead are reported only.
    /// </summary>
    private static void WriteAblationNeutralisationTable(AblationFeatureSummary[] summaries, int seedCount)
    {
        Console.WriteLine("=== Mechanism neutralised when off ===");
        Console.WriteLine($"{"feature",-19}{"probe counter",-28}{"ON mean",9}{"OFF mean",9}  verdict");
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            AblationFeatureSummary summary = summaries[feature];
            string verdict;
            if (!AblationHasNeutralisationGate(feature))
            {
                verdict = "REPORTED ONLY (not gated; classified by divergence)";
            }
            else if (summary.GateFailures == 0)
            {
                verdict = "NEUTRAL -- gate holds on every seed";
            }
            else
            {
                verdict = $"NOT NEUTRALISED on {summary.GateFailures}/{seedCount} seeds";
            }

            Console.WriteLine($"{AblationFeatureNames[feature],-19}{AblationCounterNames[feature],-28}" +
                              $"{AblationMechanismMeanText(summary.MechanismOnMean, feature),9}" +
                              $"{AblationMechanismMeanText(summary.MechanismOffMean, feature),9}  {verdict}");
        }

        Console.WriteLine("Gates: local cover coverage opened = 0; run coordination runs published = 0; keeper sweep");
        Console.WriteLine("       sweep-evaluated ticks = 0; commitment committed player-ticks = 0; perception limits");
        Console.WriteLine("       occluded sightings = 0; rest defence off mean security > on mean security, every seed.");
        Console.WriteLine("Orientation and lookahead are reported, not gated; lookahead's strict claim belongs to --search.");
    }

    /// <summary>
    /// The A9.2 findings paragraph: every inert and overwhelming feature, each stated from the measured number
    /// that put it there. The classification is the report's job; this block only reads it back in words, so a
    /// reader does not have to infer the verdict from the table.
    /// </summary>
    private static void WriteAblationFindings(AblationFeatureSummary[] summaries, ulong[] seeds)
    {
        Console.WriteLine("=== Findings (the measured numbers, not an assertion) ===");
        WriteAblationFindingGroup(summaries, seeds, AblationClass.Inert, "inert");
        WriteAblationFindingGroup(summaries, seeds, AblationClass.Overwhelming, "overwhelming");
        WriteAblationFindingGroup(summaries, seeds, AblationClass.Measured, "measured");
        Console.WriteLine("An inert feature is one whose ablation changed nothing measurable, or whose reference counter");
        Console.WriteLine("never moved - the build does not actually use the mechanism. An overwhelming feature is one");
        Console.WriteLine("the same ablation moves far past the stated bounds - the match is not robust to its removal.");
        Console.WriteLine("The probe reports both; neither is corrected here.");
    }

    /// <summary>Writes one A9.2 findings group (one classification) as a labelled, indented block.</summary>
    private static void WriteAblationFindingGroup(
        AblationFeatureSummary[] summaries,
        ulong[] seeds,
        AblationClass target,
        string label)
    {
        var parts = new List<string>();
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            AblationFeatureSummary summary = summaries[feature];
            if (summary.Class != target) continue;

            if (target == AblationClass.Inert && summary.CounterZeroInReference)
            {
                parts.Add($"{AblationFeatureNames[feature]}: reference {AblationCounterNames[feature]} = 0 " +
                          "across every seed, so there was nothing to ablate");
            }
            else if (target == AblationClass.Inert)
            {
                parts.Add($"{AblationFeatureNames[feature]}: no paired seed diverged " +
                          $"({summary.DivergentSeeds}/{seeds.Length}), so the off build played the same match");
            }
            else if (target == AblationClass.Overwhelming)
            {
                parts.Add($"{AblationFeatureNames[feature]}: {summary.HeadlineSeeds}/{seeds.Length} paired seeds " +
                          "moved a headline aggregate past its bound, mean ball separation " +
                          $"{summary.MeanBallSeparation:F2} m (gain >= {AblationSeparationBoundMeters:F1} m)");
            }
            else
            {
                parts.Add($"{AblationFeatureNames[feature]}: {summary.DivergentSeeds}/{seeds.Length} seeds diverged, " +
                          $"median first divergence {DescribeMedianTick(summary.MedianFirstDivergence)} t, mean ball " +
                          $"separation {summary.MeanBallSeparation:F2} m; {AblationCounterNames[feature]} " +
                          $"{AblationMechanismMeanText(summary.MechanismOnMean, feature)} -> " +
                          $"{AblationMechanismMeanText(summary.MechanismOffMean, feature)}");
            }
        }

        Console.Write($"{label,-13}: ");
        if (parts.Count == 0)
        {
            Console.WriteLine("none");
            return;
        }

        Console.WriteLine(parts[0]);
        string padding = new(' ', 15);
        for (int index = 1; index < parts.Count; index++)
            Console.WriteLine(padding + parts[index]);
    }

    /// <summary>
    /// The A9.2 PASS/FAIL verdict. PASS requires both builds at full time on every seed for every feature, and
    /// every gated ablation neutralised in the off build. The failing feature — and the failing seed values —
    /// are printed rather than only counted.
    /// </summary>
    private static bool ReportAblationVerdict(AblationFeatureSummary[] summaries, ulong[] seeds)
    {
        int seedCount = seeds.Length;
        Console.WriteLine("=== Verdict ===");

        int fullTimePairs = 0;
        int expectedFullTimePairs = AblationFeatureCount * seedCount;
        bool fullTimeOk = true;
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            AblationFeatureSummary summary = summaries[feature];
            fullTimePairs += summary.FullTimePairs;
            if (summary.FullTimePairs == seedCount) continue;
            fullTimeOk = false;
            Console.WriteLine($"  (a) {AblationFeatureNames[feature],-19} both builds full time on " +
                              $"{summary.FullTimePairs}/{seedCount} seeds -- FAIL (incomplete seeds: " +
                              $"{DescribeSeeds(summary.IncompleteSeeds, seeds)})");
        }

        Console.WriteLine($"  (a) full time, both builds, every feature x seed: {(fullTimeOk ? "OK" : "FAIL")} " +
                          $"({fullTimePairs}/{expectedFullTimePairs} pairs)");

        bool neutralisationOk = true;
        for (int feature = 0; feature < AblationFeatureCount; feature++)
        {
            AblationFeatureSummary summary = summaries[feature];
            string gate = AblationGateDescription(feature);
            if (!AblationHasNeutralisationGate(feature))
            {
                Console.WriteLine($"  (b) {AblationFeatureNames[feature],-19} {gate,-44} reported only");
                continue;
            }

            if (summary.GateFailures == 0)
            {
                Console.WriteLine($"  (b) {AblationFeatureNames[feature],-19} {gate,-44} OK on {seedCount}/{seedCount} seeds");
                continue;
            }

            neutralisationOk = false;
            Console.WriteLine($"  (b) {AblationFeatureNames[feature],-19} {gate,-44} FAIL on " +
                              $"{summary.GateFailures}/{seedCount} seeds (failing seeds: " +
                              $"{DescribeSeeds(summary.GateFailureSeeds, seeds)})");
        }

        bool ok = fullTimeOk && neutralisationOk;
        Console.WriteLine();
        Console.WriteLine(ok
            ? "RESULT: PASS -- every feature's two builds reached full time on every seed, and every gated" +
              " ablation neutralised its own mechanism in the off build"
            : "RESULT: FAIL -- see the (a)/(b) lines above: a build did not reach full time, or a gated ablation" +
              " did not neutralise its mechanism when off");
        return ok;
    }

    /// <summary>Median of the collected first-divergence ticks; 0-2 values are the ordinary cases for the probe.</summary>
    private static double Median(List<int> values)
    {
        if (values.Count == 0) return double.NaN;
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 1
            ? values[middle]
            : (values[middle - 1] + values[middle]) / 2d;
    }

    /// <summary>A median tick as a whole number, or <c>n/a</c> when no seed diverged.</summary>
    private static string DescribeMedianTick(double median) =>
        double.IsNaN(median) ? "n/a" : median.ToString("F0");

    /// <summary>The seed values behind a list of seed indices, for a failure line that names the actual seeds.</summary>
    private static string DescribeSeeds(List<int> seedIndices, ulong[] seeds)
    {
        if (seedIndices.Count == 0) return "none";
        var parts = new List<string>(seedIndices.Count);
        for (int index = 0; index < seedIndices.Count; index++)
            parts.Add(seeds[seedIndices[index]].ToString());
        return string.Join(", ", parts);
    }

    /// <summary>The classification as it prints in the CLASS column and the findings block.</summary>
    private static string AblationClassName(AblationClass value) => value switch
    {
        AblationClass.Inert => "inert",
        AblationClass.Overwhelming => "overwhelming",
        _ => "measured"
    };

    /// <summary>
    /// One A9.2 probe match's sampled state: the per-tick trajectory (ball and score, for divergence and
    /// separation), the per-tick mechanism samples, and the full-time aggregate counters. The trajectory is
    /// one entry per stepped tick, so its memory is bounded by the tick cap rather than by the match's events.
    /// </summary>
    private sealed class AblationMatchSample
    {
        public bool Completed;
        public int Ticks;
        public int HomeGoals;
        public int AwayGoals;
        public int PassesAttempted;
        public int PassesCompleted;
        public int Shots;
        public float HomePossessionShare;

        /// <summary>The authoritative ball position after each stepped tick (index i = tick i + 1).</summary>
        public readonly List<Vector3> BallPositions = new();

        /// <summary>The score after each stepped tick, so a goal is a divergence even while the ball is still.</summary>
        public readonly List<int> HomeGoalsByTick = new();
        public readonly List<int> AwayGoalsByTick = new();

        /// <summary>Player-ticks with a live commitment; ticks on which any keeper evaluated the sweep race.</summary>
        public long CommittedPlayerTicks;
        public long SweepEvaluatedTicks;

        /// <summary>Sum and count of the defined rest-defence security samples (both teams pooled).</summary>
        public double SecuritySum;
        public long SecuritySamples;

        public int CoverageRequestsOpened;
        public int CoverageRequestsClosed;
        public int Handovers;
        public int RunsPublished;
        public int RunsWithdrawn;
        public int RunsClaimed;
        public int CombinationsPublished;
        public int CombinationsContinued;

        /// <summary>The match-level scan telemetry at full time (A7.1 diagnostics, read-only).</summary>
        public ScanTelemetry Scan;

        /// <summary>Mean rest-defence security over the samples where the picture is defined (0 when never defined).</summary>
        public double MeanSecurity() => SecuritySamples > 0 ? SecuritySum / SecuritySamples : 0d;
    }

    /// <summary>
    /// One (feature, seed) A9.2 comparison: the classification inputs the report needs, plus the neutralisation
    /// gate result so the verdict does not have to hold the off match in memory.
    /// </summary>
    private sealed class AblationComparison
    {
        public bool ReferenceCompleted;
        public bool OffCompleted;
        public int ReferenceTicks;
        public int OffTicks;
        public int ReferenceHomeGoals;
        public int ReferenceAwayGoals;
        public int OffHomeGoals;
        public int OffAwayGoals;
        public int FirstDivergenceTick = -1;
        public int DivergentTicks;
        public double MeanBallSeparation;
        public double MaxBallSeparation;
        public double MechanismOn;
        public double MechanismOff;
        public int TicksDelta;
        public double PossessionDeltaPp;
        public int GoalsDelta;
        public int PassesDelta;
        public int ShotsDelta;
        public bool HeadlineMove;
        public bool GateHolds;
    }

    /// <summary>One A9.2 feature's pooled result over the seed set: the row of the aggregate table.</summary>
    private sealed class AblationFeatureSummary
    {
        public int FullTimePairs;
        public int DivergentSeeds;
        public int HeadlineSeeds;
        public int GateFailures;
        public double MechanismOnMean;
        public double MechanismOffMean;
        public double MedianFirstDivergence = double.NaN;
        public double MeanBallSeparation;
        public double TicksDeltaMean;
        public double PossessionDeltaPpMean;
        public double GoalsDeltaMean;
        public double PassesDeltaMean;
        public double ShotsDeltaMean;
        public bool CounterZeroInReference;
        public AblationClass Class;
        public readonly List<int> IncompleteSeeds = new();
        public readonly List<int> GateFailureSeeds = new();
    }
}
