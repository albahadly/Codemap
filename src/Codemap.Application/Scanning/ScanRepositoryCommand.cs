using Codemap.Application.Behaviors;
using Codemap.Application.Messaging;
using Codemap.Domain;

namespace Codemap.Application.Scanning;

public sealed record ScanRepositoryCommand(string Path, Language? LanguageHint) : IRequest<ScanResult>, IProgressReportingRequest
{
    public string ProgressScope => ProjectNameFor(Path);

    /// <summary>Project display name = last path segment of the scanned directory/solution.</summary>
    public static string ProjectNameFor(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

public sealed record ScanResult(TopologyGraph Graph, IReadOnlyList<string> Warnings);
