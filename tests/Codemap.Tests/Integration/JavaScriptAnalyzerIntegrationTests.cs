using Codemap.Domain;
using Codemap.Infrastructure.JavaScript;
using Jering.Javascript.NodeJS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Codemap.Tests.Integration;

/// <summary>
/// Drives the real Node bridge (Jering + TypeScript Compiler API + Acorn) against a small fixture
/// project written to a temp folder. Requires Node.js on PATH — the analyzer itself degrades to
/// warnings without it, and this test asserts the happy path since CI/dev machines have Node.
/// </summary>
public sealed class JavaScriptAnalyzerIntegrationTests : IDisposable
{
    private readonly string _fixtureDir = Path.Combine(Path.GetTempPath(), "codemap-jsfixture-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;

    public JavaScriptAnalyzerIntegrationTests()
    {
        Directory.CreateDirectory(_fixtureDir);
        var services = new ServiceCollection();
        services.AddNodeJS();
        services.Configure<NodeJSProcessOptions>(o => o.ProjectPath = JavaScriptWorkspaceAnalyzer.ScriptDirectory);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_fixtureDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Analyzes_typescript_and_javascript_fixtures_end_to_end()
    {
        File.WriteAllText(Path.Combine(_fixtureDir, "base.ts"), """
            export class BaseClient {
                protect() { }
            }
            """);
        File.WriteAllText(Path.Combine(_fixtureDir, "api.ts"), """
            import { BaseClient } from './base';

            export class ApiClient extends BaseClient {
                async loadScan(id: string) {
                    return fetch(`/api/scan/${id}`);
                }
                async startScan(path: string) {
                    return fetch('/api/scan', { method: 'POST' });
                }
            }
            """);
        File.WriteAllText(Path.Combine(_fixtureDir, "util.js"), """
            export function formatName(name) { return name.trim(); }

            export function greet(name) {
                return 'hi ' + formatName(name);
            }
            """);

        var analyzer = new JavaScriptWorkspaceAnalyzer(
            _provider.GetRequiredService<INodeJSService>(),
            NullLogger<JavaScriptWorkspaceAnalyzer>.Instance);

        var result = await analyzer.AnalyzeAsync(_fixtureDir, onProgress: null, progressBand: (0, 100), CancellationToken.None);

        Assert.True(result.Warnings.Count == 0,
            "JS analysis reported warnings (is Node.js installed?): " + string.Join("; ", result.Warnings));

        // Modules + classes + functions become nodes.
        Assert.Contains(result.Nodes, n => n is { Id: "api.ts", Kind: TypeKind.Module, Language: Language.TypeScript });
        Assert.Contains(result.Nodes, n => n is { Id: "api.ts#ApiClient", Kind: TypeKind.Class });
        Assert.Contains(result.Nodes, n => n is { Id: "util.js#formatName", Kind: TypeKind.Function, Language: Language.JavaScript });

        // class X extends Y (cross-module import) → Inherits.
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.Inherits, FromId: "api.ts#ApiClient", ToId: "base.ts#BaseClient" });

        // ES import → References between modules.
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.References, FromId: "api.ts", ToId: "base.ts" });

        // Same-module call → Calls edge.
        Assert.Contains(result.Edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "util.js#greet", ToId: "util.js#formatName" });

        // HTTP call sites: template literal normalized to a {param} segment; explicit POST method.
        Assert.Contains(result.HttpCallSites, c =>
            c.HttpMethod == "GET" && c.Url.StartsWith("/api/scan/{", StringComparison.Ordinal) && c.NodeId == "api.ts#ApiClient");
        Assert.Contains(result.HttpCallSites, c =>
            c is { HttpMethod: "POST", Url: "/api/scan" });

        var memberOwner = result.Nodes.Single(n => n.Id == "api.ts#ApiClient");
        Assert.Contains(memberOwner.Members, m => m.Signature == "loadScan(id)");
    }
}
