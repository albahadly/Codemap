using Codemap.Application.Messaging;
using Codemap.Application.Scanning;
using Microsoft.AspNetCore.SignalR;

namespace Codemap.Web.Hubs;

/// <summary>Bridges dispatcher notifications onto the SignalR hub (registered by assembly scanning).</summary>
public sealed class ScanProgressHubForwarder(IHubContext<ScanProgressHub> hubContext)
    : INotificationHandler<ScanProgressChanged>
{
    public Task Handle(ScanProgressChanged notification, CancellationToken ct = default) =>
        hubContext.Clients.All.SendAsync(
            "scanProgress",
            notification.ProjectName,
            notification.PercentComplete,
            notification.CurrentFile,
            ct);
}
