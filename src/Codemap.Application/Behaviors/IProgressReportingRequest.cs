namespace Codemap.Application.Behaviors;

/// <summary>
/// Marker for requests whose lifecycle should be broadcast as <c>ScanProgressChanged</c> notifications
/// by <see cref="ScanProgressBehavior{TRequest,TResponse}"/>. The scope names the project/scan the
/// progress belongs to so the UI can correlate updates.
/// </summary>
public interface IProgressReportingRequest
{
    string ProgressScope { get; }
}
