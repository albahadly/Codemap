using Codemap.Application.Messaging;
using Codemap.Application.Scanning;

namespace Codemap.Application.Behaviors;

/// <summary>
/// Publishes scan lifecycle progress (0% on start, 100% on completion/failure) as
/// <see cref="ScanProgressChanged"/> notifications; the Web layer forwards those to the SignalR hub.
/// Assumption: fine-grained per-file progress is published by the scan handler itself while analyzing —
/// a pipeline behavior can only observe the boundaries of a request, so it owns start/end reporting.
/// </summary>
public sealed class ScanProgressBehavior<TRequest, TResponse>(IDispatcher dispatcher)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken ct = default)
    {
        if (request is not IProgressReportingRequest reporting)
            return await next().ConfigureAwait(false);

        await dispatcher.Publish(new ScanProgressChanged(reporting.ProgressScope, 0, "Starting scan…"), ct).ConfigureAwait(false);
        try
        {
            var response = await next().ConfigureAwait(false);
            await dispatcher.Publish(new ScanProgressChanged(reporting.ProgressScope, 100, "Scan complete"), ct).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException)
        {
            // The request's own token is already canceled — report with None so the notification still goes out.
            await dispatcher.Publish(new ScanProgressChanged(reporting.ProgressScope, 100, "Scan canceled"), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await dispatcher.Publish(new ScanProgressChanged(reporting.ProgressScope, 100, "Scan failed"), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}
