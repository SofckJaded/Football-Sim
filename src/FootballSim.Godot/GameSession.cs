using FootballSim;
using FootballSim.Training;
using FootballSim.World.Bridge;
using FootballSim.World.Career;

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

    /// <summary>True when a career is loaded (the main menu shows it as the continue target).</summary>
    public static bool HasActiveCareer => ActiveCareer is not null;

    /// <summary>Clears every career reference (used when a career ends or is abandoned).</summary>
    public static void ClearCareer()
    {
        ActiveCareer = null;
        PendingCareerMatch = null;
    }

    public static void ReturnToMainMenu(Godot.SceneTree tree) =>
        tree.ChangeSceneToFile(MainMenuScene);

    /// <summary>Goes back to the career hub (or the career entry screen when no career is loaded).</summary>
    public static void ReturnToCareerHub(Godot.SceneTree tree) =>
        tree.ChangeSceneToFile(HasActiveCareer ? CareerHubScene : CareerEntryScene);
}
