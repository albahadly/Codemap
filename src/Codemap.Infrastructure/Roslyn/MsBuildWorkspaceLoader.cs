using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Codemap.Infrastructure.Roslyn;

public sealed class MsBuildWorkspaceLoader : IWorkspaceLoader
{
    private static readonly string[] IgnoredDirectories = ["bin", "obj", "node_modules", ".git", ".vs"];

    static MsBuildWorkspaceLoader()
    {
        // Must run before any MSBuild assembly is loaded into the process.
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public async Task<LoadedWorkspace> LoadAsync(string path, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var workspaces = new List<IDisposable>();
        var projects = new List<Project>();
        var seenProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in DiscoverTargets(path))
        {
            ct.ThrowIfCancellationRequested();
            var workspace = MSBuildWorkspace.Create();
            workspaces.Add(workspace);
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                {
                    lock (warnings) warnings.Add($"Workspace: {e.Diagnostic.Message}");
                }
            });

            try
            {
                if (target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    var project = await workspace.OpenProjectAsync(target, cancellationToken: ct).ConfigureAwait(false);
                    if (seenProjectPaths.Add(project.FilePath ?? project.Name)) projects.Add(project);
                }
                else
                {
                    var solution = await workspace.OpenSolutionAsync(target, cancellationToken: ct).ConfigureAwait(false);
                    foreach (var project in solution.Projects)
                    {
                        if (project.Language == LanguageNames.CSharp && seenProjectPaths.Add(project.FilePath ?? project.Name))
                            projects.Add(project);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                warnings.Add($"Failed to load '{target}': {ex.Message}");
            }
        }

        if (projects.Count == 0)
            warnings.Add($"No C# solutions or projects were loaded from '{path}'.");

        return new LoadedWorkspace(projects, warnings, workspaces);
    }

    /// <summary>
    /// A file path is used as-is. For folders: every .sln/.slnx wins; if none exist, every .csproj
    /// found outside build/temp directories is loaded individually.
    /// </summary>
    private static IReadOnlyList<string> DiscoverTargets(string path)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];

        var solutions = EnumerateFiles(path, "*.sln").Concat(EnumerateFiles(path, "*.slnx")).ToList();
        if (solutions.Count > 0) return solutions;

        return EnumerateFiles(path, "*.csproj").ToList();
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory.EnumerateFiles(root, pattern, options)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)));
    }
}
