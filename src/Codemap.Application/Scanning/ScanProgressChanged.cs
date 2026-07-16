using Codemap.Application.Messaging;

namespace Codemap.Application.Scanning;

public sealed record ScanProgressChanged(string ProjectName, int PercentComplete, string CurrentFile) : INotification;
