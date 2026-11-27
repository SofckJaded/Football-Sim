using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FootballSim.World.Data;
using FootballSim.World.World;

namespace FootballSim.World.Career;

/// <summary>
/// The W4 save/load boundary: writes a whole career (the world snapshot plus the career state) to one
/// versioned JSON file and rebuilds an advanceable <see cref="CareerSession"/> from it.
///
/// <para><b>File shape.</b> <c>{ "saveVersion": 1, "packId": ..., "packPath": ..., "world": {...},
/// "career": {...} }</c>. The file is written indentation-free; enums are stored as their numeric values
/// (ordinals are stable and documented as the save format); property names are camelCase. The pack id and
/// pack path are part of the header so a save is never silently restored against a different country: the
/// data pack supplies club identity, and a mismatched pack is rejected before the world is touched.</para>
///
/// <para><b>Atomicity.</b> A save is written to a temporary sibling file, flushed to disk, and only then
/// moved over the target. A failed save therefore leaves either the previous file or no file — never a
/// half-written career at the target path.</para>
///
/// <para><b>Save points.</b> Only between matchdays: a prepared live match or a queued match result is
/// frontend state, so <see cref="CareerSession.CanSave"/> gates both save paths.</para>
/// </summary>
public static class SaveManager
{
    /// <summary>The save-file schema version this build writes and reads.</summary>
    public const int SaveVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes a career to a save file atomically.</summary>
    /// <param name="session">The career to capture; it must be at a savable point between matchdays.</param>
    /// <param name="path">Target file path; the directory is created when missing.</param>
    /// <exception cref="ArgumentNullException">The session or the path is null.</exception>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// A live match is prepared or a match result is queued; finish the matchday first.
    /// </exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public static void Save(CareerSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The save path is empty.", nameof(path));

        string json = Serialize(session);
        WriteAtomically(path, json);
    }

    /// <summary>Reads a career back from a save file.</summary>
    /// <param name="path">The save file to read.</param>
    /// <returns>A career that continues exactly where the saved one stopped.</returns>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The path is empty.</exception>
    /// <exception cref="FileNotFoundException">The save file does not exist.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is not a valid save of this version, names another world pack, or carries an inconsistent
    /// world/career snapshot.
    /// </exception>
    public static CareerSession Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The save path is empty.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The save file was not found: {fullPath}", fullPath);
        }

        string json = File.ReadAllText(fullPath, Encoding.UTF8);
        return Deserialize(json);
    }

    /// <summary>
    /// Captures a career as the save-file JSON without touching the disk (the in-memory form of
    /// <see cref="Save"/>; useful for tests and for inspecting a save's shape).
    /// </summary>
    /// <param name="session">The career to capture; it must be at a savable point between matchdays.</param>
    /// <returns>The complete save document as JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A live match is prepared or a match result is queued; finish the matchday first.
    /// </exception>
    public static string Serialize(CareerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.CanSave)
        {
            throw new InvalidOperationException(
                "The career cannot be saved while a match is prepared or its result is queued; " +
                "finish the matchday first.");
        }

        CareerSaveData career = session.ExportSaveData();
        WorldSaveData world = WorldPersistence.Capture(session.World);
        WorldPack pack = session.World.Pack;

        var file = new SaveFileData
        {
            SaveVersion = SaveVersion,
            PackId = pack.PackId,
            PackPath = pack.SourceName ?? string.Empty,
            World = world,
            Career = career
        };

        return JsonSerializer.Serialize(file, SerializerOptions);
    }

    /// <summary>Rebuilds a career from the JSON produced by <see cref="Serialize"/>.</summary>
    /// <param name="json">The save document.</param>
    /// <returns>A career that continues exactly where the saved one stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ArgumentException">The document is empty.</exception>
    /// <exception cref="InvalidDataException">
    /// The document is not valid save JSON, has another save version, names another world pack, or carries
    /// an inconsistent world/career snapshot.
    /// </exception>
    public static CareerSession Deserialize(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("The save document is empty.", nameof(json));

        SaveFileData? file;
        try
        {
            file = JsonSerializer.Deserialize<SaveFileData>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The save document is not valid JSON.", ex);
        }

        if (file is null) throw new InvalidDataException("The save document is empty or null.");
        if (file.SaveVersion != SaveVersion)
        {
            throw new InvalidDataException(
                $"Unsupported save version {file.SaveVersion}; this build reads version {SaveVersion}.");
        }

        if (string.IsNullOrWhiteSpace(file.PackId))
        {
            throw new InvalidDataException("The save document does not name its world data pack.");
        }

        // The pack is resolved and checked before the world is deserialized in depth, so a save made
        // against another country fails on the cheap header data instead of after a full world rebuild.
        WorldPack pack = ResolvePack(file.PackPath);
        if (!string.Equals(pack.PackId, file.PackId.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The save was made for world pack '{file.PackId}' but '{pack.PackId}' was loaded " +
                $"from '{pack.SourceName}'.");
        }

        if (file.World is null) throw new InvalidDataException("The save document has no world snapshot.");
        if (file.Career is null) throw new InvalidDataException("The save document has no career state.");

        WorldState world = WorldPersistence.Restore(pack, file.World);
        return CareerSession.RestoreFromSave(world, file.Career);
    }

    /// <summary>
    /// Resolves the data pack to rebuild the world from: the saved pack path when it still exists, else the
    /// repository's default pack. The pack id check in <see cref="Deserialize"/> is what makes the fallback safe.
    /// </summary>
    private static WorldPack ResolvePack(string? packPath)
    {
        if (!string.IsNullOrWhiteSpace(packPath) && File.Exists(packPath))
        {
            return WorldPackLoader.Load(packPath);
        }

        return WorldPackLoader.Load(WorldPackPaths.ResolveDefaultPack());
    }

    /// <summary>
    /// Writes the document to a temporary sibling file, flushes it to disk, then atomically renames it over
    /// the target. Any failure removes the temporary file and leaves the target untouched.
    /// </summary>
    private static void WriteAtomically(string path, string json)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The save path has no directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);

        // Same directory as the target, so the final move is a same-volume rename (atomic on Windows and
        // Unix) rather than a copy that could be interrupted halfway.
        string temporaryPath = fullPath + ".tmp";
        try
        {
            byte[] bytes = Utf8NoBom.GetBytes(json);
            using (var stream = new FileStream(
                       temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>Deletes a file, ignoring the "already gone" and "cannot delete" outcomes.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The original save failure is the useful exception; a stray temp file is not.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The root of a save document. It is the only type with a fixed identity the file format promises; the
/// world and career graphs live behind their own DTOs.
/// </summary>
internal sealed class SaveFileData
{
    /// <summary>Save schema version (<see cref="SaveManager.SaveVersion"/>).</summary>
    public int SaveVersion { get; set; }

    /// <summary>World data-pack id the world was generated from.</summary>
    public string PackId { get; set; } = string.Empty;

    /// <summary>Path the data pack was loaded from, for mods that keep a pack outside the repository.</summary>
    public string PackPath { get; set; } = string.Empty;

    /// <summary>The complete world snapshot.</summary>
    public WorldSaveData? World { get; set; }

    /// <summary>The career half: config, identity, inbox, ledger and system counters.</summary>
    public CareerSaveData? Career { get; set; }
}
