using System;
using Godot;
using FootballSim;
using FootballSim.Training;
using FootballSim.World.Bridge;
using FootballSim.World.Career;
using FootballSim.World.Players;

namespace FootballSim.GodotClient;

/// <summary>
/// Cross-scene state: what the menu selected for the next match/training scene, plus the single-player career
/// the Career UI drives (plan stage W6). Godot's scene changes destroy the nodes, so the career and the pending
/// career match have to live here, exactly like <see cref="PendingMatch"/> does for quick matches.
/// </summary>
public static class GameSession
{
    public const string MainMenuScene = "res://MainMenu.tscn";
    public const string MatchScene = "res://MainMatchScene.tscn";
    public const string FreeTrainingScene = "res://FreeTrainingScene.tscn";
    public const string CareerEntryScene = "res://Career/CareerEntryScene.tscn";
    public const string CareerCreationScene = "res://Career/CareerCreationScene.tscn";
    public const string CareerHubScene = "res://Career/CareerHubScene.tscn";

    public static MatchSetupOptions PendingMatch { get; set; } = new();
    public static string PendingDrillId { get; set; } = FreeTrainingDrill.DrillId;

    /// <summary>
    /// The career currently being played, or null when the player is in the quick-match/practice part of the
    /// game. The Career Hub, the live-match handoff and the autosave all read this one session.
    /// </summary>
    public static CareerSession? ActiveCareer { get; set; }

    /// <summary>
    /// The career match the match scene has to play (W7), or null for a quick match. The bridge setup already
    /// carries the engine configuration built from both world squads, the human lock and the shirt mapping.
    /// </summary>
    public static WorldMatchSetup? PendingCareerMatch { get; set; }

    /// <summary>The match the career scene last played, as a one-line summary for the hub.</summary>
    public static string LastCareerMatchSummary { get; set; } = string.Empty;

    /// <summary>True when a career is loaded (the main menu shows it as the continue target).</summary>
    public static bool HasActiveCareer => ActiveCareer is not null;

    /// <summary>Clears every career reference (used when a career ends or is abandoned).</summary>
    public static void ClearCareer()
    {
        ActiveCareer = null;
        PendingCareerMatch = null;
        LastCareerMatchSummary = string.Empty;
    }

    public static void ReturnToMainMenu(Godot.SceneTree tree) =>
        tree.ChangeSceneToFile(MainMenuScene);

    /// <summary>Goes back to the career hub (or the career entry screen when no career is loaded).</summary>
    public static void ReturnToCareerHub(Godot.SceneTree tree) =>
        tree.ChangeSceneToFile(HasActiveCareer ? CareerHubScene : CareerEntryScene);

    // ---------------------------------------------------------------------------------------------
    // Dev-only boot hooks for the W7 scene smoke check.
    //
    // A 3D match scene cannot be scene-changed into headlessly (and a boot scene changing into it stalls
    // the renderer setup), so the gate launches MainMatchScene.tscn directly with a user argument and lets
    // the scene configure itself. No game menu ever sets these flags.
    // ---------------------------------------------------------------------------------------------

    /// <summary>True when the process was launched with <c>--boot-quick-match</c> (dev smoke check).</summary>
    public static bool DevBootQuickMatch { get; private set; }

    /// <summary>True when the process was launched with <c>--boot-career-match</c> (dev smoke check).</summary>
    public static bool DevBootCareerMatch { get; private set; }

    /// <summary>
    /// Applies the dev boot user arguments (called once by the match scene). With no arguments this is a
    /// no-op, so a normal launch is unaffected.
    /// </summary>
    /// <param name="carry">Error text when the dev career setup failed, otherwise empty.</param>
    /// <returns>True when a dev boot mode was requested.</returns>
    public static bool ApplyDevBootArgs(out string error)
    {
        error = string.Empty;
        string[] args = OS.GetCmdlineUserArgs();
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--boot-quick-match") DevBootQuickMatch = true;
            if (args[index] == "--boot-career-match") DevBootCareerMatch = true;
        }

        if (DevBootQuickMatch && !DevBootCareerMatch)
        {
            PendingCareerMatch = null;
            PendingMatch = new MatchSetupOptions
            {
                HumanPlayerId = MatchPositions.DefaultPlayerId,
                HalfDurationSeconds = 20f
            };
            return true;
        }

        if (!DevBootCareerMatch) return false;

        try
        {
            var config = new CareerConfig
            {
                WorldSeed = 20261127UL,
                FirstName = "Boot",
                LastName = "Match",
                StartingAge = 17,
                Backstory = BackstoryKind.LocalClubYouth
            };

            CareerSession career = CareerSession.StartNewCareer(config);
            ActiveCareer = career;

            CareerAdvanceResult matchday = career.AdvanceToNextEventOrMatchday();
            for (int step = 0; step < 40 && matchday.Kind != CareerEventKind.HumanMatchday; step++)
            {
                if (matchday.Kind == CareerEventKind.InboxAction && matchday.Message is { } message)
                {
                    career.RespondToOffer(message.Id, accept: false);
                }

                matchday = career.AdvanceToNextEventOrMatchday();
            }

            if (matchday.Kind != CareerEventKind.HumanMatchday)
            {
                error = $"the dev career never reached a matchday (stopped at {matchday.Kind})";
                return true;
            }

            PendingCareerMatch = career.PrepareHumanLiveMatchSetup(
                halfDurationSeconds: 20f, forceHumanStarter: true);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.IO.IOException or FootballSim.World.Data.WorldPackException)
        {
            error = $"{exception.GetType().Name}: {exception.Message}";
            return true;
        }
    }
}
