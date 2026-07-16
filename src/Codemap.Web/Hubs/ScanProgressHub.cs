using Microsoft.AspNetCore.SignalR;

namespace Codemap.Web.Hubs;

/// <summary>Live scan progress channel; the UI subscribes with a HubConnection and the
/// ScanProgressChanged notification handler broadcasts into it.</summary>
public sealed class ScanProgressHub : Hub;
