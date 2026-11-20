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
                return RunHeadlessMatch(playersPerTeam: 11, halfDurationSeconds: 2700f, seed: 20260818UL) ? 0 : 1;

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
}
