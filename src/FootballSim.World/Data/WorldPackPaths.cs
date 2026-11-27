namespace FootballSim.World.Data;

/// <summary>
/// Finds world data packs at development time.
///
/// Packs live at the repository root (<c>data/world/</c>) rather than in a project's output folder, so
/// a test host or a headless run started from <c>bin/Debug/net8.0</c> must walk up to the checkout to
/// find one. The walk is deliberately marker-based (the solution file) instead of a fixed number of
/// <c>..</c> segments: a test assembly one folder deeper must not start silently loading a different
/// file. Game builds copy the pack next to the executable (see the World project is data-only), and an
/// absolute path or the first ancestor containing the pack both work.
/// </summary>
public static class WorldPackPaths
{
    /// <summary>Pack the prototype loads unless a caller names another one.</summary>
    public const string DefaultPackRelativePath = "data/world/default-country.json";

    private static readonly string[] RootMarkers =
    {
        "FootballSimulation.sln",
        DefaultPackRelativePath
    };

    /// <summary>
    /// Resolves <see cref="DefaultPackRelativePath"/> to an existing absolute path.
    /// </summary>
    /// <exception cref="FileNotFoundException">The pack could not be found from the current location.</exception>
    public static string ResolveDefaultPack() => Resolve(DefaultPackRelativePath);

    /// <summary>
    /// Resolves a path relative to the repository root (or to an ancestor that contains it). An
    /// absolute path is returned only if the file exists.
    /// </summary>
    /// <exception cref="FileNotFoundException">No candidate location contains the file.</exception>
    public static string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            string absolute = Path.GetFullPath(relativePath);
            if (File.Exists(absolute)) return absolute;
            throw new FileNotFoundException($"World data file not found: {absolute}", absolute);
        }

        var searched = new List<string>();
        foreach (string root in CandidateRoots())
        {
            string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            searched.Add(candidate);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            $"World data file '{relativePath}' was not found. Searched:{Environment.NewLine}" +
            string.Join(Environment.NewLine, searched));
    }

    /// <summary>
    /// The repository root: the first ancestor of the test host or working directory that contains the
    /// solution file or the default pack.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">No marker file was found above the process location.</exception>
    public static string FindRepositoryRoot()
    {
        var searched = new List<string>();
        foreach (string root in CandidateRoots())
        {
            searched.Add(root);
            foreach (string marker in RootMarkers)
            {
                if (File.Exists(Path.Combine(root, marker))) return root;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root (no " + string.Join(" or ", RootMarkers) +
            " found above the process location). Searched:" + Environment.NewLine +
            string.Join(Environment.NewLine, searched));
    }

    /// <summary>Candidate directories, nearest first: the process folder and the working directory, then ancestors.</summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] starts = { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

        foreach (string start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;

            DirectoryInfo? directory;
            try
            {
                directory = new DirectoryInfo(start);
            }
            catch (ArgumentException)
            {
                continue;
            }

            while (directory is not null)
            {
                if (seen.Add(directory.FullName)) yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
