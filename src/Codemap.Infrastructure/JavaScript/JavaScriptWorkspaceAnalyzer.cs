using System.Diagnostics;
using System.Text.Json;
using Codemap.Application.Abstractions;
using Codemap.Domain;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.Logging;

namespace Codemap.Infrastructure.JavaScript;

/// <summary>
/// JS/TS side of the scan. Discovers source files under the path, ships them to a bundled Node script
/// (TypeScript Compiler API for .ts/.tsx/.jsx, Acorn for plain .js) via Jering.Javascript.NodeJS, and
/// maps the JSON payload back onto the domain model. Degrades to warnings (never a failed scan) when
/// Node or the npm packages are unavailable.
/// </summary>
public sealed class JavaScriptWorkspaceAnalyzer(INodeJSService nodeJs, ILogger<JavaScriptWorkspaceAnalyzer> logger)
{
    private static readonly string[] IgnoredDirectories =
        ["node_modules", "bin", "obj", "dist", "build", "out", "coverage", ".git", ".vs"];

    private static readonly SemaphoreSlim NpmInstallLock = new(1, 1);
    private static bool _npmInstallAttempted;

    public static string ScriptDirectory => Path.Combine(AppContext.BaseDirectory, "JavaScript", "scripts");

    public async Task<AnalysisResult> AnalyzeAsync(
        string path,
        Func<ScanProgress, Task>? onProgress,
        (int From, int To) progressBand,
        CancellationToken ct = default)
    {
        var rootPath = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
        var (tsFiles, jsFiles) = DiscoverFiles(rootPath);
        if (tsFiles.Count == 0 && jsFiles.Count == 0)
            return AnalysisResult.Empty;

        if (onProgress is not null)
            await onProgress(new ScanProgress(progressBand.From, $"Analyzing {tsFiles.Count + jsFiles.Count} JS/TS files…")).ConfigureAwait(false);

        var warnings = new List<string>();
        if (!await EnsureNodePackagesAsync(warnings, ct).ConfigureAwait(false))
            return AnalysisResult.Empty with { Warnings = warnings };

        var optionsJson = JsonSerializer.Serialize(new { rootPath, tsFiles, jsFiles });
        JsAnalysisPayload? payload;
        try
        {
            payload = await nodeJs.InvokeFromFileAsync<JsAnalysisPayload>(
                Path.Combine(ScriptDirectory, "analyzer.js"),
                args: [optionsJson],
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JS/TS analysis failed; continuing with C# results only.");
            warnings.Add($"JS analysis unavailable ({ex.GetType().Name}): {ex.Message}");
            return AnalysisResult.Empty with { Warnings = warnings };
        }

        if (payload is null)
            return AnalysisResult.Empty with { Warnings = warnings };

        var result = Map(payload, warnings);
        if (onProgress is not null)
            await onProgress(new ScanProgress(progressBand.To, $"JS/TS analysis complete ({result.Nodes.Count} nodes)")).ConfigureAwait(false);
        return result;
    }

    private static (List<string> TsFiles, List<string> JsFiles) DiscoverFiles(string rootPath)
    {
        var tsFiles = new List<string>();
        var jsFiles = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var file in Directory.EnumerateFiles(rootPath, "*.*", options))
        {
            var relative = Path.GetRelativePath(rootPath, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => IgnoredDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                continue;

            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".ts" or ".tsx" or ".jsx":
                    if (!file.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)) tsFiles.Add(file);
                    break;
                case ".js" or ".mjs" or ".cjs":
                    jsFiles.Add(file);
                    break;
            }
        }
        return (tsFiles, jsFiles);
    }

    /// <summary>Runs `npm install` in the bundled scripts folder once per process if node_modules is missing.</summary>
    private static async Task<bool> EnsureNodePackagesAsync(List<string> warnings, CancellationToken ct)
    {
        if (Directory.Exists(Path.Combine(ScriptDirectory, "node_modules", "typescript"))) return true;

        await NpmInstallLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(Path.Combine(ScriptDirectory, "node_modules", "typescript"))) return true;
            if (_npmInstallAttempted)
            {
                warnings.Add("JS analysis skipped: npm packages could not be installed earlier in this session.");
                return false;
            }
            _npmInstallAttempted = true;

            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", "/c npm install --no-audit --no-fund")
                : new ProcessStartInfo("npm", "install --no-audit --no-fund");
            startInfo.WorkingDirectory = ScriptDirectory;
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                warnings.Add("JS analysis skipped: could not start npm to install analyzer dependencies.");
                return false;
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                warnings.Add($"JS analysis skipped: npm install failed ({stderr.Trim()}).");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            warnings.Add($"JS analysis skipped: npm bootstrap failed ({ex.Message}).");
            return false;
        }
        finally
        {
            NpmInstallLock.Release();
        }
    }

    private static AnalysisResult Map(JsAnalysisPayload payload, List<string> warnings)
    {
        warnings.AddRange(payload.Warnings ?? []);

        var nodes = new List<CodeNode>();
        foreach (var jsNode in payload.Nodes ?? [])
        {
            if (!Enum.TryParse<TypeKind>(jsNode.Kind, ignoreCase: true, out var kind)) continue;
            var language = Enum.TryParse<Language>(jsNode.Language, ignoreCase: true, out var lang)
                ? lang
                : Language.JavaScript;

            nodes.Add(new CodeNode(
                jsNode.Id,
                jsNode.DisplayName,
                kind,
                language,
                jsNode.Namespace ?? string.Empty,
                (jsNode.Members ?? []).Select(m => new MemberSignature(m.Signature, m.ReturnOrType ?? string.Empty, m.IsStatic)).ToList(),
                jsNode.SourceFile,
                jsNode.LineNumber));
        }

        var knownIds = nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var edges = (payload.Edges ?? [])
            .Where(e => Enum.TryParse<EdgeKind>(e.Kind, ignoreCase: true, out _))
            .Select(e => new CodeEdge(e.FromId, e.ToId, Enum.Parse<EdgeKind>(e.Kind, ignoreCase: true), e.Detail))
            .Where(e => !e.FromId.Equals(e.ToId, StringComparison.Ordinal))
            .Where(e => knownIds.Contains(e.FromId) && knownIds.Contains(e.ToId))
            .Distinct()
            .ToList();

        var callSites = (payload.CallSites ?? [])
            .Where(c => knownIds.Contains(c.NodeId))
            .Select(c => new HttpCallSite(c.NodeId, c.HttpMethod, c.Url, c.SourceFile, c.LineNumber))
            .ToList();

        return new AnalysisResult(nodes, edges, [], callSites, warnings);
    }

    internal sealed record JsAnalysisPayload(
        List<JsNode>? Nodes,
        List<JsEdge>? Edges,
        List<JsCallSite>? CallSites,
        List<string>? Warnings);

    internal sealed record JsNode(
        string Id,
        string DisplayName,
        string Kind,
        string Language,
        string? Namespace,
        List<JsMember>? Members,
        string SourceFile,
        int LineNumber);

    internal sealed record JsMember(string Signature, string? ReturnOrType, bool IsStatic);

    internal sealed record JsEdge(string FromId, string ToId, string Kind, string? Detail);

    internal sealed record JsCallSite(string NodeId, string HttpMethod, string Url, string SourceFile, int LineNumber);
}
