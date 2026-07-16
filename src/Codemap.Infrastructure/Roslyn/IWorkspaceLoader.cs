using Microsoft.CodeAnalysis;

namespace Codemap.Infrastructure.Roslyn;

/// <summary>
/// Loads Roslyn projects from a path. Assumption: the spec lists IWorkspaceLoader with the
/// Application-layer interfaces, but its surface is Roslyn-typed (<see cref="Project"/>), which must not
/// leak into Codemap.Application — so it lives here and the Application-facing seam stays IWorkspaceAnalyzer.
/// </summary>
public interface IWorkspaceLoader
{
    /// <param name="path">A .sln/.slnx/.csproj file, or a folder to discover solutions/projects in.</param>
    Task<LoadedWorkspace> LoadAsync(string path, CancellationToken ct = default);
}

/// <summary>Loaded projects plus load diagnostics; disposal releases the underlying MSBuild workspaces.</summary>
public sealed class LoadedWorkspace(
    IReadOnlyList<Project> projects,
    IReadOnlyList<string> warnings,
    IReadOnlyList<IDisposable> ownedWorkspaces) : IDisposable
{
    public IReadOnlyList<Project> Projects { get; } = projects;
    public IReadOnlyList<string> Warnings { get; } = warnings;

    public void Dispose()
    {
        foreach (var workspace in ownedWorkspaces) workspace.Dispose();
    }
}
