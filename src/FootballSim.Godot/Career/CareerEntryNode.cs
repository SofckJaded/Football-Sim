using System;
using Godot;
using FootballSim.World.Career;

namespace FootballSim.GodotClient;

/// <summary>
/// Career entry screen (plan stage W6): continue the latest save, load one of the save slots, start a new
/// career, or go back to the main menu. It never touches the simulation itself — it only loads a
/// <see cref="CareerSession"/> into <see cref="GameSession.ActiveCareer"/> and switches to the hub.
/// </summary>
public partial class CareerEntryNode : Control
{
    private VBoxContainer _pages = null!;

    public override void _Ready()
    {
        _pages = CareerUi.BuildChrome(this, minimumWidth: 940f);
        ShowMenu();
    }

    public override void _Input(InputEvent @event)
    {
        bool back = @event switch
        {
            InputEventKey key => key.Pressed && !key.Echo && key.Keycode == Key.Escape,
            InputEventJoypadButton pad => pad.Pressed && pad.ButtonIndex == JoyButton.B,
            _ => false
        };

        if (back)
        {
            GameSession.ReturnToMainMenu(GetTree());
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>Focuses the first action once the page's nodes are inside the tree.</summary>
    private void FinishPage() => Callable.From(() => CareerUi.FocusFirstIfNone(this)).CallDeferred();

    private void ShowMenu()
    {
        CareerUi.Clear(_pages);
        CareerUi.Heading(_pages, "CAREER", 20);
        CareerUi.Paragraph(
            _pages,
            "One player, one club at a time, one living world of 108 clubs across six divisions. " +
            "Your career is saved in its own slots; the quick-match and practice modes are separate.");

        int? latest = CareerSaveFiles.LatestSlot();
        if (latest is int latestSlot && CareerSaveFiles.Info(latestSlot) is { } info)
        {
            CareerUi.Button(
                _pages,
                $"Continue — {info.Describe()}",
                () => Continue(latestSlot),
                primary: true);
        }
        else
        {
            CareerUi.Paragraph(_pages, "No career saves yet. Start a new career to begin.", 13,
                CareerUi.MutedColor);
        }

        CareerUi.Button(_pages, "New Career", () => GetTree().ChangeSceneToFile(GameSession.CareerCreationScene),
            primary: latest is null);

        bool anySlot = latest is not null;
        CareerUi.Button(_pages, "Load Save Slot", ShowLoadSlots, enabled: anySlot);
        CareerUi.Button(_pages, "Delete Save", ShowDeleteSlots, enabled: anySlot);
        CareerUi.Button(_pages, "Back to Main Menu", () => GameSession.ReturnToMainMenu(GetTree()));

        if (GameSession.HasActiveCareer)
        {
            CareerSession active = GameSession.ActiveCareer!;
            CareerUi.Paragraph(
                _pages,
                $"A career is already loaded: {active.HumanPlayer.DisplayName} at " +
                $"{active.HumanClub?.Name ?? "no club"}, season {active.Season}.",
                13, CareerUi.Accent);
            CareerUi.Button(_pages, "Resume Loaded Career", () =>
                GetTree().ChangeSceneToFile(GameSession.CareerHubScene));
        }

        FinishPage();
    }

    private void ShowLoadSlots()
    {
        CareerUi.Clear(_pages);
        CareerUi.Heading(_pages, "LOAD CAREER", 20);
        CareerUi.Paragraph(_pages, "Choose a save slot.", 13, CareerUi.MutedColor);

        for (int slot = 0; slot <= CareerSaveFiles.SlotCount; slot++)
        {
            CareerSaveInfo? info = CareerSaveFiles.Info(slot);
            string label = info is null
                ? $"Slot {SlotLabel(slot)} — empty"
                : $"Slot {SlotLabel(slot)} — {info.Describe()}";
            int captured = slot;
            CareerUi.Button(_pages, label, () => Continue(captured), enabled: info is not null);
        }

        CareerUi.Button(_pages, "Back", ShowMenu);
        FinishPage();
    }

    private void ShowDeleteSlots()
    {
        CareerUi.Clear(_pages);
        CareerUi.Heading(_pages, "DELETE CAREER", 20);
        CareerUi.Paragraph(_pages, "Deleting a slot cannot be undone.", 13, CareerUi.WarnColor);

        for (int slot = 0; slot <= CareerSaveFiles.SlotCount; slot++)
        {
            CareerSaveInfo? info = CareerSaveFiles.Info(slot);
            if (info is null) continue;
            int captured = slot;
            CareerUi.Button(_pages, $"Delete slot {SlotLabel(slot)} — {info.Describe()}", () =>
            {
                CareerSaveFiles.Delete(captured);
                ShowDeleteSlots();
            });
        }

        CareerUi.Button(_pages, "Back", ShowMenu);
        FinishPage();
    }

    private void Continue(int slot)
    {
        CareerSession? session = CareerSaveFiles.TryLoad(slot, out string error);
        if (session is null)
        {
            CareerUi.Clear(_pages);
            CareerUi.Heading(_pages, "LOAD FAILED", 20);
            CareerUi.Paragraph(_pages, error, 14, CareerUi.WarnColor);
            CareerUi.Button(_pages, "Back", ShowMenu, primary: true);
            FinishPage();
            return;
        }

        GameSession.ActiveCareer = session;
        GameSession.PendingCareerMatch = null;
        GetTree().ChangeSceneToFile(GameSession.CareerHubScene);
    }

    private static string SlotLabel(int slot) => slot == CareerSaveFiles.AutosaveSlot ? "A" : slot.ToString();
}
