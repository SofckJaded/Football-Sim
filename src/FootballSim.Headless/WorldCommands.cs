using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using FootballSim.Core;
using FootballSim.World.Career;
using FootballSim.World.Core;
using FootballSim.World.Data;
using FootballSim.World.Players;
using FootballSim.World.Simulation;
using FootballSim.World.World;

namespace FootballSim.Headless;

/// <summary>
/// Living-world / career CLI commands (plan stage W8): <c>--world-soak</c> for multi-season world-health
/// telemetry and <c>--world-career-sim</c> for a readable single-player career biography. Both are pure
/// diagnostics on top of the deterministic simulation: they print the same numbers a save would continue from
/// and never change sim behaviour.
/// </summary>
public static class WorldCommands
{
    /// <summary>Default root seed both commands use (the plan's prototype date).</summary>
    public const ulong DefaultSeed = 20261127UL;

    /// <summary>Default seasons for <c>--world-soak</c>.</summary>
    public const int DefaultSoakSeasons = 20;

    /// <summary>Default seasons for <c>--world-career-sim</c>.</summary>
    public const int DefaultCareerSeasons = 10;

    /// <summary>
    /// Runs <c>--world-soak [seasons=20] [seed=20261127]</c>: generates the 108-club world, plays N full
    /// seasons through the W3 ecosystem (matches, cup, growth, transfers, contracts, retirements, intakes,
    /// manager carousel, rollover) and prints world-health telemetry.
    /// </summary>
    /// <param name="args">The raw command-line arguments (index 0 is the command name).</param>
    /// <returns>0 on success, 1 when the soak produced an unhealthy world.</returns>
    public static int RunSoak(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int seasons = DefaultSoakSeasons;
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeasons))
        {
            seasons = Math.Clamp(parsedSeasons, 1, 50);
        }

        ulong seed = DefaultSeed;
        if (args.Length > 2 && ulong.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsedSeed))
        {
            seed = parsedSeed;
        }

        WorldPack pack = WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack());
        var stopwatch = Stopwatch.StartNew();
        WorldState world = WorldGenerator.Generate(pack, new WorldGenerationOptions { Seed = seed });
        var ecosystem = new WorldEcosystem(world);
        ecosystem.EnsureInitialized();
        long generationMs = stopwatch.ElapsedMilliseconds;

        long totalLeagueGoals = 0;
        long totalLeagueMatches = 0;
        long totalCupTies = 0;
        long totalTransfers = 0;
        long totalRetirements = 0;
        long totalIntakes = 0;
        long totalPromotions = 0;
        long totalRelegations = 0;
        long totalManagerChanges = 0;
        ClubId lastCupChampion = ClubId.None;
        int lastCupChampionSeason = 0;

        Console.WriteLine($"== World soak: {seasons} season(s), seed {seed} ==");
        Console.WriteLine(
            $"World: {world.Country.Name} | {world.Clubs.Count} clubs | {world.Players.Count} players | " +
            $"{pack.Divisions.Count} divisions");
        Console.WriteLine($"SimulationVersion: {SimulationVersion.Tag}");
        Console.WriteLine();

        for (int season = 1; season <= seasons; season++)
        {
            SeasonSummary summary = ecosystem.PlaySeasonWithRollover(out SeasonRolloverSummary rollover);
            totalLeagueGoals += summary.LeagueGoals;
            totalLeagueMatches += summary.LeagueMatchesPlayed;
            totalCupTies += summary.CupTiesPlayed;
            totalTransfers += rollover.Transfers;
            totalRetirements += rollover.Retirements;
            totalIntakes += rollover.YouthIntakes;
            totalManagerChanges += rollover.ManagerChanges;
            if (summary.CupChampionClubId.IsValid)
            {
                lastCupChampion = summary.CupChampionClubId;
                lastCupChampionSeason = summary.Season;
            }

            foreach (DivisionMove move in summary.DivisionMoves)
            {
                if (move.ToTier < move.FromTier) totalPromotions++;
                else if (move.ToTier > move.FromTier) totalRelegations++;
            }

            Console.WriteLine(
                $"Season {summary.Season,2}: {summary.LeagueMatchesPlayed,4} league matches, " +
                $"{summary.LeagueGoals,4} goals ({summary.GoalsPerMatch:0.000}/match), cup ties " +
                $"{summary.CupTiesPlayed,3}, transfers {rollover.Transfers,4}, retirements " +
                $"{rollover.Retirements,4}, intakes {rollover.YouthIntakes,3}, managers " +
                $"{rollover.ManagerChanges,3}");
        }

        stopwatch.Stop();
        long simulationMs = stopwatch.ElapsedMilliseconds - generationMs;

        Console.WriteLine();
        Console.WriteLine("== World health ==");
        Console.WriteLine($"Active population:      {ecosystem.ActivePopulation()}");
        Console.WriteLine($"Mean age:               {ecosystem.MeanActiveAge():0.00}");
        Console.WriteLine(
            $"Age distribution:       " +
            string.Join("  ", AgeDistribution(world)));
        Console.WriteLine(
            $"Tier mean OVR:          " +
            string.Join("  ", Enumerable.Range(1, 6).Select(tier =>
                $"T{tier} {ecosystem.TierMeanOverall(tier):0.0}")));
        bool tiersOrdered = ecosystem.TierMeanOverall(1) > ecosystem.TierMeanOverall(3) &&
                            ecosystem.TierMeanOverall(3) > ecosystem.TierMeanOverall(6);
        Console.WriteLine($"Tier separation T1>T3>T6: {tiersOrdered}");
        Console.WriteLine(
            $"Goals per league match: {(totalLeagueMatches == 0 ? 0d : totalLeagueGoals / (double)totalLeagueMatches):0.000} " +
            $"({totalLeagueGoals} goals in {totalLeagueMatches} matches; cup ties {totalCupTies})");
        Console.WriteLine($"Promotions / relegations: {totalPromotions} / {totalRelegations}");
        Console.WriteLine(
            $"Transfers / retirements / youth intakes: {totalTransfers} / {totalRetirements} / {totalIntakes}");
        Console.WriteLine($"Manager changes:        {totalManagerChanges}");
        Console.WriteLine($"Cup champion:           {ClubName(world, lastCupChampion)} (season {lastCupChampionSeason})");
        Console.WriteLine($"Top scorer:             {TopScorerLine(world)}");
        Console.WriteLine();
        Console.WriteLine($"World generation:       {generationMs} ms");
        Console.WriteLine($"Simulation:             {simulationMs} ms " +
                          $"({(seasons == 0 ? 0d : simulationMs / (double)seasons):0} ms/season)");
        Console.WriteLine($"Save snapshot size:     {EstimateSaveBytes(world) / 1024} KiB (estimated JSON floor)");

        bool healthy = tiersOrdered &&
                       ecosystem.ActivePopulation() is >= 2_200 and <= 3_100 &&
                       ecosystem.MeanActiveAge() is >= 22.5 and <= 27.5;
        Console.WriteLine();
        Console.WriteLine(healthy ? "SOAK RESULT: HEALTHY" : "SOAK RESULT: OUTSIDE THE PLAN'S HEALTH BANDS");
        return healthy ? 0 : 1;
    }

    /// <summary>
    /// Runs <c>--world-career-sim [seasons=10] [mode=authentic|custom] [backstory=elite|regional|local|street|multisport] [potential=journeyman|regional|prospect|star|generational]</c>:
    /// plays a full single-player career through the public <see cref="CareerSession"/> facade and prints a
    /// readable biography.
    /// </summary>
    /// <param name="args">The raw command-line arguments (index 0 is the command name).</param>
    /// <returns>0 on success (a completed career run), 1 on a failed run.</returns>
    public static int RunCareerSim(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        int seasons = DefaultCareerSeasons;
        if (args.Length > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSeasons))
        {
            seasons = Math.Clamp(parsedSeasons, 1, 30);
        }

        CareerCreationMode mode = args.Length > 2 &&
            args[2].Equals("custom", StringComparison.OrdinalIgnoreCase)
                ? CareerCreationMode.Custom
                : CareerCreationMode.Authentic;

        BackstoryKind backstory = args.Length > 3 ? ParseBackstory(args[3]) : BackstoryKind.LocalClubYouth;
        PotentialPreset potential = args.Length > 4 ? ParsePotential(args[4]) : PotentialPreset.ProProspect;

        var config = new CareerConfig
        {
            WorldSeed = DefaultSeed,
            Mode = mode,
            FirstName = "CLI",
            LastName = "Career",
            StartingAge = 17,
            Backstory = backstory,
            Potential = mode == CareerCreationMode.Custom ? potential : null,
            InitialTrainingFocus = TrainingFocus.Balanced
        };

        var stopwatch = Stopwatch.StartNew();
        CareerSession career;
        try
        {
            career = CareerSession.StartNewCareer(config);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.IO.IOException or WorldPackException)
        {
            Console.WriteLine($"Career could not be started: {exception.Message}");
            return 1;
        }

        var seasonRows = new List<string>();
        var roleBySeason = new List<string>();
        var honours = new List<string>();
        var trainingBySeason = new List<string>();
        int seasonsRolled = 0;
        int guard = 0;

        float startTopSpeed = career.HumanPlayer.Attributes.Current(AttributeId.TopSpeed);
        float startPassing = career.HumanPlayer.Attributes.Current(AttributeId.Passing);
        float startFinishing = career.HumanPlayer.Attributes.Current(AttributeId.Finishing);
        float startComposure = career.HumanPlayer.Attributes.Current(AttributeId.Composure);
        float ceilingTopSpeed = career.HumanPlayer.Attributes.Ceiling(AttributeId.TopSpeed);
        float ceilingPassing = career.HumanPlayer.Attributes.Ceiling(AttributeId.Passing);
        float ceilingFinishing = career.HumanPlayer.Attributes.Ceiling(AttributeId.Finishing);
        float ceilingComposure = career.HumanPlayer.Attributes.Ceiling(AttributeId.Composure);

        while (seasonsRolled < seasons && !career.IsCareerOver && guard++ < 50_000)
        {
            CareerAdvanceResult result = career.AdvanceToNextEventOrMatchday();
            switch (result.Kind)
            {
                case CareerEventKind.HumanMatchday:
                {
                    IReadOnlyList<CareerMatchResult> matches = career.ResolveHumanMatchViaQuickSim();
                    _ = matches;
                    break;
                }

                case CareerEventKind.InboxAction:
                    AnswerInbox(career, result.Message!);
                    break;

                case CareerEventKind.NeedsClub:
                {
                    CareerInboxMessage invite = career.RequestAmateurTrial();
                    career.RespondToOffer(invite.Id, accept: true);
                    break;
                }

                case CareerEventKind.SeasonRolledOver:
                {
                    seasonsRolled++;
                    CareerSeasonRecord record = career.History.Seasons[^1];
                    seasonRows.Add(DescribeSeason(record));
                    foreach (string honour in record.Honours) honours.Add($"S{record.Season}: {honour}");

                    EmergentIdentity identity = IdentityEvaluator.Evaluate(career.HumanPlayer);
                    roleBySeason.Add($"S{record.Season}: {identity.PrimaryRole} ({identity.TacticalRole})");

                    TrainingFocus recommended = TrainingPlanner.RecommendedFocusFor(career.HumanPlayer);
                    career.SetTrainingFocus(recommended);
                    trainingBySeason.Add($"S{record.Season}: {recommended}");
                    break;
                }

                case CareerEventKind.CareerOver:
                    break;
            }
        }

        stopwatch.Stop();

        PlayerPublicView view = career.GetPlayerPublicView();
        WorldPlayer player = career.HumanPlayer;
        int bestTier = career.History.BestTier;
        string status = FinalStatus(career, bestTier);

        Console.WriteLine("== World career simulation ==");
        Console.WriteLine($"SimulationVersion: {SimulationVersion.Tag}");
        Console.WriteLine(
            $"Players: {player.DisplayName} | mode {config.Mode} | backstory {config.Backstory} | " +
            $"start age {config.StartingAge} | seed {config.WorldSeed}");
        Console.WriteLine(
            $"World: {career.World.Country.Name}, {career.World.Clubs.Count} clubs, " +
            $"{career.Ecosystem.ActivePopulation()} active players " +
            $"({career.World.Players.Count} registered including retired)");
        Console.WriteLine($"Seasons played: {career.History.SeasonsPlayed} | runtime {stopwatch.ElapsedMilliseconds} ms");
        Console.WriteLine();

        Console.WriteLine("== Season ledger ==");
        foreach (string row in seasonRows) Console.WriteLine(row);
        Console.WriteLine();
        Console.WriteLine(
            $"Career totals: {career.History.TotalAppearances} apps, {career.History.TotalGoals} goals, " +
            $"{career.History.TotalAssists} assists, {career.History.CareerAverageRating:0.00} average rating");
        Console.WriteLine($"Best division reached: tier {bestTier}");
        Console.WriteLine($"Final club: {career.HumanClub?.Name ?? "free agent"} | status: {career.SquadStatus}");

        Console.WriteLine();
        Console.WriteLine("== Attribute trajectory (current / ceiling) ==");
        Console.WriteLine(
            $"Top speed  {startTopSpeed,5:0.0} -> {player.Attributes.Current(AttributeId.TopSpeed),5:0.0} / {ceilingTopSpeed:0.0}");
        Console.WriteLine(
            $"Passing    {startPassing,5:0.0} -> {player.Attributes.Current(AttributeId.Passing),5:0.0} / {ceilingPassing:0.0}");
        Console.WriteLine(
            $"Finishing  {startFinishing,5:0.0} -> {player.Attributes.Current(AttributeId.Finishing),5:0.0} / {ceilingFinishing:0.0}");
        Console.WriteLine(
            $"Composure  {startComposure,5:0.0} -> {player.Attributes.Current(AttributeId.Composure),5:0.0} / {ceilingComposure:0.0}");
        Console.WriteLine($"Overall now: {player.Attributes.MeanCurrent():0.0}");

        Console.WriteLine();
        Console.WriteLine("== Emergent identity over time ==");
        foreach (string role in roleBySeason) Console.WriteLine(role);
        Console.WriteLine($"Current style tags: " +
                          (view.StyleTags.Count == 0 ? "(none)" : string.Join(", ", view.StyleTags)));
        Console.WriteLine($"Coach view: {view.CoachReport.Summary}");

        Console.WriteLine();
        Console.WriteLine("== Training focuses chosen ==");
        foreach (string training in trainingBySeason) Console.WriteLine(training);

        Console.WriteLine();
        Console.WriteLine("== Honours ==");
        if (honours.Count == 0) Console.WriteLine("(none)");
        else foreach (string honour in honours) Console.WriteLine(honour);

        Console.WriteLine();
        Console.WriteLine($"FINAL STATUS: {status}");
        Console.WriteLine(
            status == "Star" ? "A top-flight star who made the most of his ceiling."
            : status == "Solid Pro" ? "A settled professional who played most of his career in the upper pyramid."
            : status == "Journeyman" ? "A hard-working lower-league career with real minutes and a few moves."
            : "An amateur lifer who kept playing because he loved it.");

        return career.History.SeasonsPlayed > 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------
    // Career policy and formatting.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Answers an actionable career message with a sensible CLI policy: accept renewals and trials, accept a
    /// move only when it is upwards (a higher division), decline loans and sideways/downward offers.
    /// </summary>
    private static void AnswerInbox(CareerSession career, CareerInboxMessage message)
    {
        bool accept = message switch
        {
            ContractRenewalMessage => true,
            TrialInviteMessage => true,
            TransferOfferMessage transfer => IsUpwardMove(career, transfer.ToClubId),
            LoanOfferMessage => false,
            _ => false
        };

        if (!career.RespondToOffer(message.Id, accept))
        {
            _ = career.RespondToOffer(message.Id, accept: false);
        }
    }

    private static bool IsUpwardMove(CareerSession career, ClubId toClubId)
    {
        int currentTier = career.HumanClub?.Tier ?? 7;
        if (!career.World.Clubs.TryGet(toClubId, out WorldClub? club) || club is null) return false;
        return club.Tier < currentTier;
    }

    private static string DescribeSeason(CareerSeasonRecord record)
    {
        string honours = record.Honours.Count == 0
            ? string.Empty
            : " | " + string.Join(", ", record.Honours);
        string movement = record.DivisionMovement.Length == 0 ? string.Empty : $" | {record.DivisionMovement}";
        return
            $"S{record.Season,2} {record.ClubName,-24} T{record.Tier} " +
            $"{record.Appearances,3} apps ({record.Starts,3} starts) {record.MinutesPlayed,5} min " +
            $"{record.Goals,3}G {record.Assists,3}A rating {record.AverageRating,4:0.00}{honours}{movement}";
    }

    private static string FinalStatus(CareerSession career, int bestTier)
    {
        float averageRating = career.History.CareerAverageRating;
        int appearances = career.History.TotalAppearances;
        bool starSeason = career.History.Seasons.Any(season =>
            season.Honours.Count > 0 && season.AverageRating >= 7.2f && season.Appearances >= 20);

        if (bestTier <= 1 && (averageRating >= 7.0f || appearances >= 150 || starSeason)) return "Star";
        if (bestTier <= 2 && appearances >= 40) return "Solid Pro";
        if (bestTier <= 4 && appearances >= 20) return "Journeyman";
        return "Amateur Lifer";
    }

    private static IEnumerable<string> AgeDistribution(WorldState world)
    {
        int[] buckets = new int[6];
        string[] labels = { "<=18", "19-21", "22-24", "25-27", "28-30", "31+" };
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            int age = player.Age;
            int bucket = age <= 18 ? 0 : age <= 21 ? 1 : age <= 24 ? 2 : age <= 27 ? 3 : age <= 30 ? 4 : 5;
            buckets[bucket]++;
        }

        for (int index = 0; index < buckets.Length; index++)
        {
            yield return $"{labels[index]} {buckets[index]}";
        }
    }

    private static string TopScorerLine(WorldState world)
    {
        PlayerId best = PlayerId.None;
        int goals = 0;
        foreach (WorldPlayer player in world.Players.All)
        {
            if (player.IsRetired) continue;
            if (player.SeasonHistory.Count == 0) continue;
            PlayerSeasonStats last = player.SeasonHistory[^1];
            if (last.Goals > goals)
            {
                goals = last.Goals;
                best = player.Id;
            }
        }

        return best.IsValid ? $"{world.Players[best].DisplayName} ({goals} goals)" : "(n/a)";
    }

    private static string ClubName(WorldState world, ClubId clubId) =>
        clubId.IsValid && world.Clubs.TryGet(clubId, out WorldClub? club) && club is not null ? club.Name : "(none)";

    /// <summary>
    /// A rough floor for the JSON save size: one player record dominates, so this multiplies the player count
    /// by a measured per-player byte count. It is a diagnostic expectation, not a real serialization.
    /// </summary>
    private static long EstimateSaveBytes(WorldState world) => world.Players.Count * 1_800L;

    private static BackstoryKind ParseBackstory(string value) => value.ToLowerInvariant() switch
    {
        "street" or "streetfootball" => BackstoryKind.StreetFootball,
        "elite" or "eliteacademy" => BackstoryKind.EliteAcademy,
        "regional" or "regionalacademy" => BackstoryKind.RegionalAcademy,
        "multisport" or "multisportlatestarter" => BackstoryKind.MultisportLateStarter,
        _ => BackstoryKind.LocalClubYouth
    };

    private static PotentialPreset ParsePotential(string value) => value.ToLowerInvariant() switch
    {
        "journeyman" => PotentialPreset.Journeyman,
        "regional" or "regionalstandout" => PotentialPreset.RegionalStandout,
        "star" or "topflightstar" => PotentialPreset.TopFlightStar,
        "generational" => PotentialPreset.Generational,
        _ => PotentialPreset.ProProspect
    };
}
