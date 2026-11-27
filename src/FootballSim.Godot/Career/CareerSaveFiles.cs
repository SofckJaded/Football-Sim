using System;
using System.IO;
using System.Text.Json;
using Godot;
using FootballSim.World.Career;

namespace FootballSim.GodotClient;

/// <summary>Display metadata for one career save slot (read from a small sidecar, never by parsing the save).</summary>
/// <param name="Slot">Slot number (0 = autosave, 1..3 = player slots).</param>
/// <param name="PlayerName">Career player's display name.</param>
/// <param name="ClubName">Current club, or "Free agent".</param>
/// <param name="Season">Saved world season.</param>
/// <param name="Week">Saved world week.</param>
/// <param name="SavedAtUtc">When the save was written.</param>
/// <param name="Bytes">Save-file size in bytes.</param>
public sealed record CareerSaveInfo(
    int Slot,
    string PlayerName,
    string ClubName,
    int Season,
    int Week,
    DateTime SavedAtUtc,
    long Bytes)
{
    /// <summary>Human line shown on the entry screen.</summary>
    public string Describe() =>
        $"{PlayerName} — {ClubName}, season {Season} week {Week} ({SavedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
}

/// <summary>Sidecar metadata written next to a career save so the entry screen never has to parse ~12 MB.</summary>
public sealed class CareerSaveMeta
{
    public int Version { get; set; } = 1;
    public string PlayerName { get; set; } = string.Empty;
    public string ClubName { get; set; } = string.Empty;
    public int Season { get; set; }
    public int Week { get; set; }
    public string SavedAtUtc { get; set; } = string.Empty;
}

/// <summary>
/// Career save slots in the Godot user directory (plan stage W6). The living-world save itself is written by
/// <see cref="SaveManager"/> (versioned JSON, atomic write); this class only chooses the paths, keeps a small
/// metadata sidecar for the entry screen and turns I/O failures into UI-readable messages.
/// </summary>
public static class CareerSaveFiles
{
    /// <summary>Number of player-selectable slots.</summary>
    public const int SlotCount = 3;

    /// <summary>Slot the career autosaves into (season rollovers, committed matches).</summary>
    public const int AutosaveSlot = 0;

    private const string SlotPrefix = "career-slot-";

    /// <summary>Absolute path of one slot's save file.</summary>
    public static string SlotPath(int slot) => Path.Combine(DirectoryPath(), $"{SlotPrefix}{slot}.json");

    /// <summary>Absolute path of one slot's metadata sidecar.</summary>
    public static string MetaPath(int slot) => Path.Combine(DirectoryPath(), $"{SlotPrefix}{slot}.meta.json");

    /// <summary>True when the slot has a save file.</summary>
    public static bool Exists(int slot) => File.Exists(SlotPath(slot));

    /// <summary>
    /// Saves the career into a slot and refreshes its metadata sidecar.
    /// </summary>
    /// <param name="session">The career to save; it must be in a savable state.</param>
    /// <param name="slot">Slot number 0..3.</param>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The slot is outside 0..3.</exception>
    /// <exception cref="InvalidOperationException">The career cannot be saved right now (see <see cref="CareerSession.CanSave"/>).</exception>
    public static void Save(CareerSession session, int slot)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateSlot(slot);

        SaveManager.Save(session, SlotPath(slot));
        WriteMeta(session, slot);
    }

    /// <summary>Loads a career from a slot.</summary>
    /// <param name="slot">Slot number 0..3.</param>
    /// <param name="error">A human-readable error when the load failed, otherwise an empty string.</param>
    /// <returns>The loaded career, or null when the slot is empty or unreadable.</returns>
    public static CareerSession? TryLoad(int slot, out string error)
    {
        ValidateSlot(slot);
        error = string.Empty;

        if (!Exists(slot))
        {
            error = "That save slot is empty.";
            return null;
        }

        try
        {
            return SaveManager.Load(SlotPath(slot));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            UnauthorizedAccessException or InvalidOperationException or JsonException or
            ArgumentException)
        {
            error = $"The save could not be loaded: {exception.Message}";
            return null;
        }
    }

    /// <summary>Reads one slot's metadata, or null when the slot is empty/unreadable.</summary>
    public static CareerSaveInfo? Info(int slot)
    {
        ValidateSlot(slot);

        string savePath = SlotPath(slot);
        if (!File.Exists(savePath)) return null;

        var fileInfo = new FileInfo(savePath);
        CareerSaveMeta? meta = null;
        try
        {
            if (File.Exists(MetaPath(slot)))
            {
                meta = JsonSerializer.Deserialize<CareerSaveMeta>(File.ReadAllText(MetaPath(slot)));
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            meta = null;
        }

        return new CareerSaveInfo(
            slot,
            meta?.PlayerName ?? "Unknown player",
            meta?.ClubName ?? "Unknown club",
            meta?.Season ?? 0,
            meta?.Week ?? 0,
            DateTime.TryParse(meta?.SavedAtUtc, null, System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime savedAt)
                ? savedAt
                : fileInfo.LastWriteTimeUtc,
            fileInfo.Length);
    }

    /// <summary>The most recently written save slot (0..3), or null when every slot is empty.</summary>
    public static int? LatestSlot()
    {
        int? best = null;
        DateTime bestTime = DateTime.MinValue;

        for (int slot = 0; slot <= SlotCount; slot++)
        {
            CareerSaveInfo? info = Info(slot);
            if (info is null) continue;
            if (best is null || info.SavedAtUtc > bestTime)
            {
                best = slot;
                bestTime = info.SavedAtUtc;
            }
        }

        return best;
    }

    /// <summary>Deletes both the save and its sidecar for one slot.</summary>
    public static void Delete(int slot)
    {
        ValidateSlot(slot);
        TryDelete(SlotPath(slot));
        TryDelete(MetaPath(slot));
    }

    /// <summary>True when a career can be autosaved right now.</summary>
    public static bool CanAutosave(CareerSession? session) => session is { CanSave: true, IsCareerOver: false };

    /// <summary>Autosaves into the autosave slot, swallowing (and returning) any failure for UI display.</summary>
    /// <param name="session">The career to autosave.</param>
    /// <param name="error">A human-readable error when the autosave failed, otherwise empty.</param>
    /// <returns>True when the autosave was written.</returns>
    public static bool TryAutosave(CareerSession? session, out string error)
    {
        error = string.Empty;
        if (session is null) return false;
        if (!CanAutosave(session))
        {
            error = "The career cannot be autosaved while a match is in progress.";
            return false;
        }

        try
        {
            Save(session, AutosaveSlot);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException)
        {
            error = $"Autosave failed: {exception.Message}";
            return false;
        }
    }

    private static void WriteMeta(CareerSession session, int slot)
    {
        var meta = new CareerSaveMeta
        {
            PlayerName = session.HumanPlayer.DisplayName,
            ClubName = session.HumanClub?.Name ?? "Free agent",
            Season = session.Season,
            Week = session.Date.Week,
            SavedAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        };

        File.WriteAllText(MetaPath(slot), JsonSerializer.Serialize(meta));
    }

    private static string DirectoryPath()
    {
        string directory = ProjectSettings.GlobalizePath("user://career");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ValidateSlot(int slot)
    {
        if (slot < 0 || slot > SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, $"Career slots are 0..{SlotCount}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A locked save is not a reason to fail the UI action; the slot just stays.
        }
    }
}
