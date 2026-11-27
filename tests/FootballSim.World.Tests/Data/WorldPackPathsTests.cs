using FootballSim.World.Data;
using Xunit;

namespace FootballSim.World.Tests.Data;

/// <summary>
/// Pack discovery from a test host: the data pack lives at the repository root, so these tests prove the
/// marker-based ancestor walk finds it from the test output folder rather than depending on the current
/// directory.
/// </summary>
public sealed class WorldPackPathsTests
{
    [Fact]
    public void ResolveDefaultPack_FindsTheShippedPack()
    {
        string path = WorldPackPaths.ResolveDefaultPack();

        Assert.True(File.Exists(path), path);
        Assert.Contains(
            Path.Combine("data", "world"),
            Path.GetDirectoryName(path) ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("default-country.json", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindRepositoryRoot_ContainsSolutionAndPack()
    {
        string root = WorldPackPaths.FindRepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "FootballSimulation.sln")));
        Assert.True(File.Exists(Path.Combine(root, WorldPackPaths.DefaultPackRelativePath)));
    }

    [Fact]
    public void Resolve_RelativePath_MatchesDefaultPack()
    {
        string relative = WorldPackPaths.Resolve(WorldPackPaths.DefaultPackRelativePath);
        string direct = WorldPackPaths.ResolveDefaultPack();

        Assert.Equal(Path.GetFullPath(direct), Path.GetFullPath(relative));
    }

    [Fact]
    public void Resolve_MissingRelativePath_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => WorldPackPaths.Resolve("data/world/does-not-exist.json"));
    }

    [Fact]
    public void Resolve_MissingRootedPath_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"no-pack-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => WorldPackPaths.Resolve(path));
    }

    [Fact]
    public void Resolve_BlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => WorldPackPaths.Resolve(" "));
    }
}
