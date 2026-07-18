namespace Acta.Emit.Shared;

/// <summary>
/// Locates the repo root by walking up to find <c>Acta.slnx</c>.
/// </summary>
internal static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Acta.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate Acta.slnx walking up from " + AppContext.BaseDirectory);
    }
}
