# Copilot Instructions for Codemap

## What this project is

Codemap statically analyzes C# solutions and JavaScript/TypeScript projects and renders an
interactive dependency graph in the browser (Blazor Server + hand-rolled SVG canvas). Nodes are
classes/interfaces/enums/structs/functions/modules; edges are typed (`Inherits`, `Implements`,
`Calls`, `References`, `Invokes`). `Invokes` edges are **cross-language HTTP calls**: a JS `fetch`/
`axios` call site matched against an ASP.NET `[Route]`/`[Http*]` attribute. Graph snapshots persist
to SQL Server so two topologies can be diffed git-style.

## Architecture — Clean Architecture, 4 projects

```
src/Codemap.Domain           entities (CodeNode, CodeEdge, TopologyGraph), enums, pure graph
                             algorithms (TopologyDiffer, CycleDetector) — NO dependencies
src/Codemap.Application      commands/queries + handlers, custom CQRS dispatcher (Messaging/),
                             pipeline behaviors, infrastructure interfaces (Abstractions/)
src/Codemap.Infrastructure   Roslyn C# analysis (Roslyn/), JS/TS analysis via Node bridge
                             (JavaScript/), EF Core persistence (Persistence/), cross-language
                             edge resolver (CrossLanguage/)
src/Codemap.Web              Blazor Server UI (Components/), SignalR hub (Hubs/), keyboard
                             shortcut services (Services/), client interop (wwwroot/js/codemap.js)
tests/Codemap.Tests          xUnit tests for all layers
```

**Dependency direction (never violate):** `Web → Application → Domain` and
`Infrastructure → Application → Domain`. `Domain` references nothing. `Application` never
references `Infrastructure` — it defines interfaces in `Abstractions/` that `Infrastructure`
implements. `Web` wires everything in `Program.cs`.

## Hard rules and conventions

- **All use cases go through the custom `IDispatcher`** (`Codemap.Application.Messaging`) — there
  is deliberately **no MediatR**. UI components inject `IDispatcher` and call
  `Dispatcher.Send(new SomeCommand(...))`; they never call repositories or analyzers directly.
- **No external JavaScript libraries.** The graph canvas, pan/zoom, drag, and keyboard interop in
  `wwwroot/js/codemap.js` are hand-rolled by design. Do not add D3, cytoscape, npm UI packages, etc.
  (The only Node packages are `typescript` and `acorn`, used by the bundled analyzer script.)
- **Graceful database degradation.** SQL Server being offline must never break scanning: scan
  results live in `InMemoryScanResultStore`; history/persistence writes are best-effort
  (`try/catch` + `LogWarning`). Preserve this behavior in anything touching persistence.
- **Domain stays pure.** Records only, no I/O, no framework references. Graph algorithms in
  `Domain/Graph/` are static and deterministic.
- C# 13, nullable reference types enabled, **nullable warnings are errors**
  (`Directory.Build.props`). Use records for messages/DTOs, primary constructors for services,
  file-scoped namespaces, collection expressions (`[.. a, .. b]`).
- Comments explain *why* (constraints, invariants), not *what*. Match the existing terse style.

## Adding a new use case (the standard pattern)

1. Define a record implementing `IRequest<TResponse>` in `Codemap.Application/<Area>/`.
2. Implement `IRequestHandler<TRequest, TResponse>` next to it, depending only on `Abstractions/`
   interfaces and `Domain` types.
3. If it needs a new capability, add an interface to `Application/Abstractions/` and implement it
   in `Infrastructure`, registering it in `InfrastructureServiceCollectionExtensions`.
4. Handlers are auto-discovered: `AddCustomDispatcher(...)` in `Program.cs` scans the Application
   and Web assemblies. Long-running requests that should stream progress implement
   `IProgressReportingRequest` (see `ScanProgressBehavior`).
5. Notifications (`INotification`) fan out to all `INotificationHandler<T>`s — e.g.
   `ScanProgressChanged` → `ScanProgressHubForwarder` → SignalR clients.

## How analysis works (for context when editing analyzers)

- **C#:** `MsBuildWorkspaceLoader` (MSBuildLocator + `MSBuildWorkspace`) loads the target
  solution/project → `SymbolWalker` (SyntaxWalker + SemanticModel) emits nodes, member signatures,
  and ASP.NET route attributes → `CallGraphBuilder` resolves invocations/base lists/member types
  into edges. Edges point at open generic definitions; partial classes collapse into one node.
- **JS/TS:** `JavaScriptWorkspaceAnalyzer` invokes the bundled Node script
  (`Infrastructure/JavaScript/scripts/analyzer.js`) via `Jering.Javascript.NodeJS`. TypeScript
  Compiler API handles `.ts`/`.tsx`/`.jsx`; Acorn handles plain `.js`. On first JS scan it runs
  `npm install` in the script folder; missing Node degrades to a scan warning, never a crash.
- **Cross-language:** `CrossLanguageEdgeResolver` matches JS HTTP call sites (literal or simple
  template URLs) against C# route attributes by normalized route pattern (case-insensitive,
  parameter names ignored) and emits `Invokes` edges; unmatched calls become scan warnings.
- **Progress contract:** `ScanProgressBehavior` owns 0% and 100%; analyzers report within 2–95
  (`CompositeWorkspaceAnalyzer` splits that range between engines).

## Build, test, run

```bash
dotnet build                          # .NET 10 SDK required
dotnet test                           # xUnit; integration tests self-scan this repo
dotnet run --project src/Codemap.Web  # then open the printed localhost URL
```

EF Core migrations (schema auto-applies at startup):

```bash
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/Codemap.Infrastructure --startup-project src/Codemap.Web --output-dir Persistence/Migrations
```

## Testing conventions

- xUnit, no mocking framework — small hand-written fakes inside test files.
- Roslyn tests compile C# snippets in-memory via `RoslynTestHelper` and assert on emitted
  nodes/edges — follow that pattern instead of loading real projects.
- New behavior needs tests at the layer that owns it: graph algorithms → `Domain/`, dispatcher
  changes → `Messaging/DispatcherTests`, analyzer changes → `Roslyn/` or the integration tests,
  shortcut changes → `Web/ShortcutMapperTests` (and mirror key handling in `codemap.js`).

## Things that bite

- `KeyboardShortcutService`/`ShortcutMapper` (C#) and `shouldPreventDefault` in `codemap.js` must
  stay in sync — a shortcut added on one side only will either not fire or leak browser defaults.
- Node IDs must stay stable across scans (C#: fully qualified metadata-ish names via `SymbolIds`;
  JS: file-relative module paths) — snapshot diffing keys on them.
- Per-frame canvas interaction (pan/zoom/drag) is client-side only; .NET is notified solely of
  discrete events (node selected, drag finished). Don't add per-pointer-move server round-trips.
- `appsettings.Development.json` is git-ignored on purpose (machine-specific connection strings) —
  never commit one or reference it in docs as required.
