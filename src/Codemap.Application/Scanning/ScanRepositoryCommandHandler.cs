using Codemap.Application.Abstractions;
using Codemap.Application.Messaging;
using Codemap.Domain;
using Microsoft.Extensions.Logging;

namespace Codemap.Application.Scanning;

public sealed class ScanRepositoryCommandHandler(
    IWorkspaceAnalyzer analyzer,
    ICrossLanguageEdgeResolver crossLanguageResolver,
    IScanResultStore scanResultStore,
    IScanHistoryRepository scanHistory,
    IDispatcher dispatcher,
    ILogger<ScanRepositoryCommandHandler> logger) : IRequestHandler<ScanRepositoryCommand, ScanResult>
{
    public async Task<ScanResult> Handle(ScanRepositoryCommand request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            throw new ArgumentException("Scan path must not be empty.", nameof(request));
        if (!Directory.Exists(request.Path) && !File.Exists(request.Path))
            throw new DirectoryNotFoundException($"Path not found: {request.Path}");

        var projectName = request.ProgressScope;
        var scanId = Guid.NewGuid();
        await RecordHistoryAsync(() => scanHistory.RecordStartAsync(scanId, request.Path, DateTimeOffset.UtcNow, ct))
            .ConfigureAwait(false);

        try
        {
            var analysis = await analyzer.AnalyzeAsync(
                request.Path,
                request.LanguageHint,
                progress => dispatcher.Publish(
                    new ScanProgressChanged(projectName, progress.PercentComplete, progress.CurrentFile), ct),
                ct).ConfigureAwait(false);

            var resolution = crossLanguageResolver.Resolve(analysis.HttpEndpoints, analysis.HttpCallSites);
            var edges = analysis.Edges.Concat(resolution.Edges).Distinct().ToList();

            var graph = new TopologyGraph(scanId, projectName, DateTimeOffset.UtcNow, analysis.Nodes, edges);
            scanResultStore.Add(graph);

            await RecordHistoryAsync(() => scanHistory.RecordCompletionAsync(
                scanId, graph.Nodes.Count, graph.Edges.Count, "Completed", ct)).ConfigureAwait(false);

            return new ScanResult(graph, [.. analysis.Warnings, .. resolution.Warnings]);
        }
        catch (OperationCanceledException)
        {
            await RecordHistoryAsync(() => scanHistory.RecordCompletionAsync(
                scanId, 0, 0, "Canceled", CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await RecordHistoryAsync(() => scanHistory.RecordCompletionAsync(
                scanId, 0, 0, $"Failed: {ex.Message}", CancellationToken.None)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Scan history is best-effort: a missing/offline database must never fail a scan,
    /// since scan results live in the in-memory store until published.</summary>
    private async Task RecordHistoryAsync(Func<Task> record)
    {
        try
        {
            await record().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write scan history (database offline?); continuing.");
        }
    }
}
