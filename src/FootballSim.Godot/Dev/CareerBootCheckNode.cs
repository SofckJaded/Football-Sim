using System;
using System.Linq;
using Godot;
using FootballSim.World.Career;
using FootballSim.World.Players;

namespace FootballSim.GodotClient;

/// <summary>
/// Development-only smoke check for the career UI (plan stage W6 gate): creates a real career, prints a
/// machine-readable line, then switches to the career hub so the scene graph, every hub panel and the
/// save/autosave path are exercised in one headless run. It ships with the project as a dev tool (like the
/// pitch capture/dump scenes) and is never reachable from the game menus.
///
/// <para>Run with <c>Godot.exe --headless --path src/FootballSim.Godot res://Dev/CareerBootCheckScene.tscn
/// --quit-after 360</c>. A clean run prints <c>CAREER_BOOT:</c> lines and no ERROR/SCRIPT ERROR lines.</para>
/// </summary>
public partial class CareerBootCheckNode : Node
{
    public override void _Ready()
    {
        Callable.From(Run).CallDeferred();
    }

    private void Run()
    {
        // Capture the tree before any scene change: this node is freed by ChangeSceneToFile, and GetTree()
        // on a freed node returns null.
        SceneTree tree = GetTree();
        try
        {
            var config = new CareerConfig
            {
                WorldSeed = 20261127UL,
                FirstName = "Boot",
                LastName = "Check",
                StartingAge = 17,
                Backstory = BackstoryKind.LocalClubYouth,
                Mode = CareerCreationMode.Authentic
            };

            CareerSession session = CareerSession.StartNewCareer(config);
            GameSession.ActiveCareer = session;
            GameSession.PendingCareerMatch = null;

            GD.Print(
                $"CAREER_BOOT: session ok player={session.HumanPlayer.DisplayName} " +
                $"club={session.HumanClub?.Name ?? "free agent"} season={session.Season} " +
                $"fixtures={session.World.CurrentSeasonFixtures().Count()}");
            GD.Print($"CAREER_BOOT: public view ok role={session.GetPlayerPublicView().PrimaryRole}");
            GD.Print($"CAREER_BOOT: inbox ok messages={session.Inbox.Count}");
            GD.Print($"CAREER_BOOT: save path ok slot={CareerSaveFiles.SlotPath(CareerSaveFiles.AutosaveSlot)}");

            // Save/load through the real slot files, then clean the dev slots up so a developer's own
            // "Continue" target is never replaced by this smoke check.
            bool autosaved = CareerSaveFiles.TryAutosave(session, out string autosaveError);
            GD.Print($"CAREER_BOOT: autosave ok={autosaved} {autosaveError}");
            CareerSaveFiles.Save(session, 1);
            CareerSession? reloaded = CareerSaveFiles.TryLoad(1, out string loadError);
            GD.Print(
                $"CAREER_BOOT: save/load ok={reloaded is not null} " +
                $"player={reloaded?.HumanPlayer.DisplayName ?? loadError} " +
                $"bytes={new System.IO.FileInfo(CareerSaveFiles.SlotPath(1)).Length}");
            CareerSaveFiles.Delete(CareerSaveFiles.AutosaveSlot);
            CareerSaveFiles.Delete(1);

            GD.Print("CAREER_BOOT: switching to hub");
            tree.ChangeSceneToFile(GameSession.CareerHubScene);

            tree.CreateTimer(0.75).Timeout += () =>
            {
                if (tree.CurrentScene is CareerHubNode hub)
                {
                    hub.BootCheckExercisePanels();
                    GD.Print("CAREER_BOOT: hub panels exercised");
                }
                else
                {
                    GD.PrintErr($"CAREER_BOOT: hub scene missing (current={tree.CurrentScene?.Name})");
                    tree.Quit(1);
                }
            };

            tree.CreateTimer(1.5).Timeout += () =>
            {
                GD.Print("CAREER_BOOT: done");
                tree.Quit();
            };
        }
        catch (Exception exception)
        {
            GD.PrintErr($"CAREER_BOOT: FAILED {exception.GetType().Name}: {exception.Message}");
            tree.Quit(1);
        }
    }
}
