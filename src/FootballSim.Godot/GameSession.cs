using FootballSim;
using FootballSim.Training;

namespace FootballSim.GodotClient;

/// <summary>
/// Cross-scene state: what the menu selected for the next match/training scene.
/// </summary>
public static class GameSession
{
    public const string MainMenuScene = "res://MainMenu.tscn";
    public const string MatchScene = "res://MainMatchScene.tscn";
    public const string FreeTrainingScene = "res://FreeTrainingScene.tscn";

    public static MatchSetupOptions PendingMatch { get; set; } = new();
    public static string PendingDrillId { get; set; } = FreeTrainingDrill.DrillId;

    public static void ReturnToMainMenu(Godot.SceneTree tree) =>
        tree.ChangeSceneToFile(MainMenuScene);
}
