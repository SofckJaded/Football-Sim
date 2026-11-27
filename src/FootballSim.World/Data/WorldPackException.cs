namespace FootballSim.World.Data;

/// <summary>
/// Raised when a world data pack fails validation. Carries every validation error rather than only the
/// first: a mod author fixing a pack should see all of them in one pass, and a test can assert on them.
/// </summary>
public sealed class WorldPackException : Exception
{
    public WorldPackException(string sourceName, IReadOnlyList<string> errors)
        : base(BuildMessage(sourceName, errors))
    {
        SourceName = sourceName;
        Errors = errors;
    }

    /// <summary>File (or caller label) the pack came from.</summary>
    public string SourceName { get; }

    /// <summary>Every validation error found, in discovery order.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string BuildMessage(string sourceName, IReadOnlyList<string> errors)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("World data pack '").Append(sourceName).Append("' is invalid (")
            .Append(errors.Count).Append(errors.Count == 1 ? " error):" : " errors):");
        for (int i = 0; i < errors.Count; i++)
        {
            builder.AppendLine();
            builder.Append("  - ").Append(errors[i]);
        }
        return builder.ToString();
    }
}
