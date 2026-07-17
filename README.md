# Codemap — Code Structure & Relationship Analyzer

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Blazor Server](https://img.shields.io/badge/UI-Blazor%20Server-5C2D91)](https://learn.microsoft.com/aspnet/core/blazor/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Codemap statically analyzes **C# solutions** and **JavaScript/TypeScript projects** and renders an
interactive dependency graph: classes, interfaces, enums, structs, functions and modules as nodes;
inheritance, implementation, calls, references and **cross-language HTTP invocations** as typed
edges. Graph snapshots persist to SQL Server so topologies can be diffed over time, git-style.

## The idea in one picture

Point Codemap at a repository. It reads the code (it never runs it), builds a typed graph of
everything it finds, and shows it on a pannable/zoomable canvas — including the edges that
normally stay invisible: the `fetch('/api/orders')` in your frontend that lands on
`OrdersController` in your backend.

```mermaid
flowchart LR
    repo["📁 Your repository<br/>C# + JS/TS"] --> scan["🔍 Static analysis<br/>Roslyn + TypeScript API"]
    scan --> model["🕸️ Typed graph<br/>nodes + edges"]
    model --> canvas["🖥️ Interactive canvas<br/>pan · zoom · filter · inspect"]
    model --> db[("💾 SQL Server<br/>snapshots")]
    db --> diff["📊 Diff two snapshots<br/>added / removed / changed"]
    diff --> canvas
```

**What the graph contains:**

| Element | Meaning | Example |
|---|---|---|
| Node | Class, interface, enum, struct, JS/TS module or function | `OrderService`, `api/client.ts` |
| `Inherits` edge | Class inheritance | `AdminUser → User` |
| `Implements` edge | Interface implementation | `EfGraphRepository → IGraphRepository` |
| `Calls` edge | Method/function invocation | `OrderService → EmailSender` |
| `References` edge | Member types, imports, cross-module use | `import { api } from './client'` |
| `Invokes` edge | **Cross-language HTTP call** | JS `fetch('/api/scan')` → C# `[HttpPost("/api/scan")]` |

## How a scan works

1. Paste a path to a repo / `.sln` / project folder and hit **Analyze** (or `Ctrl+Enter`).
2. The C# engine (Roslyn) and the JS/TS engine (a bundled Node script) walk the code and emit
   nodes and edges; progress streams live to the browser over SignalR.
3. The cross-language resolver matches JS HTTP call sites against ASP.NET route attributes and
   adds `Invokes` edges.
4. The finished graph appears on the canvas. **Publish snapshot** persists it; **History** lets
   you diff any two snapshots, with additions/removals/changes highlighted on the canvas.

```mermaid
sequenceDiagram
    actor U as User
    participant W as Blazor UI (Home.razor)
    participant D as IDispatcher
    participant H as ScanRepositoryCommandHandler
    participant CS as Roslyn analyzer (C#)
    participant JS as Node analyzer (JS/TS)
    participant X as Cross-language resolver
    participant S as SignalR hub

    U->>W: paste path, click Analyze
    W->>D: Send(ScanRepositoryCommand)
    D->>H: dispatch through pipeline behaviors
    H->>CS: analyze .sln / .csproj
    CS-->>S: progress % + current file
    S-->>W: live progress bar
    H->>JS: analyze .ts/.tsx/.jsx/.js
    JS-->>S: progress % + current file
    H->>X: match fetch/axios URLs ↔ [Route]/[Http*] attributes
    X-->>H: Invokes edges + warnings
    H-->>W: TopologyGraph (nodes + edges)
    W-->>U: interactive graph on the canvas
```

## Architecture (Clean Architecture, 4 projects)

```mermaid
flowchart TB
    subgraph Presentation
        Web["<b>Codemap.Web</b><br/>Blazor Server UI · SignalR hub<br/>hand-rolled SVG canvas (no JS libs)"]
    end
    subgraph "Use cases"
        App["<b>Codemap.Application</b><br/>commands / queries / handlers<br/>custom CQRS dispatcher (no MediatR)<br/>interfaces in Abstractions/"]
    end
    subgraph "Technical detail"
        Infra["<b>Codemap.Infrastructure</b><br/>Roslyn analysis · Node bridge (TS API + Acorn)<br/>EF Core persistence · cross-language resolver"]
    end
    subgraph Core
        Domain["<b>Codemap.Domain</b><br/>CodeNode · CodeEdge · TopologyGraph<br/>TopologyDiffer · CycleDetector<br/>zero dependencies"]
    end

    Web --> App --> Domain
    Infra --> App
    Infra --> Domain
```

Dependency direction: `Web → Application → Domain`; `Infrastructure → Application → Domain`.
Every use case is dispatched through the custom `IDispatcher` (no MediatR) — `Codemap.Web` never
calls a service directly. `Application` owns the interfaces; `Infrastructure` implements them.

```
src/Codemap.Domain           entities, value objects, enums, pure graph algorithms — no dependencies
src/Codemap.Application      commands/queries + handlers, custom CQRS dispatcher, infrastructure interfaces
src/Codemap.Infrastructure   Roslyn analysis, JS/TS analysis (Node bridge), EF Core persistence,
                             cross-language edge resolver
src/Codemap.Web              Blazor Server UI, SignalR scan-progress hub
tests/Codemap.Tests          xUnit tests for all layers
```

## Cross-language edges — the special sauce

Codemap connects a frontend to its backend by matching HTTP call sites to route attributes:

```mermaid
flowchart LR
    subgraph "JavaScript / TypeScript"
        js["orders.ts<br/><code>fetch('/api/orders/' + id)</code>"]
    end
    subgraph Matching
        norm["normalize both sides<br/>case-insensitive ·<br/>parameter names ignored<br/><code>/api/orders/{*}</code>"]
    end
    subgraph "C# (ASP.NET)"
        cs["OrdersController.cs<br/><code>[HttpGet(&quot;/api/orders/{id}&quot;)]</code>"]
    end
    js --> norm --> cs
    js -.->|"<b>Invokes</b> edge<br/>GET /api/orders/{id}"| cs
```

`fetch`/`axios`/`$http` call sites with literal (or simple template) URLs are matched against
`[Route]`/`[Http*]` attributes by normalized route pattern; unmatched calls surface as scan
warnings instead of silently disappearing.

## Prerequisites

- **.NET 10 SDK** (MSBuildLocator loads MSBuild from the installed SDK to open target solutions)
- **SQL Server** — defaults to `(localdb)\MSSQLLocalDB` (see `ConnectionStrings:Codemap` in
  `src/Codemap.Web/appsettings.json`). If the database is unreachable the app still runs; scans
  stay in memory and only publish/history features are disabled.
- **Node.js** — only needed for JS/TS analysis. On first JS scan, Codemap runs `npm install`
  inside the bundled analyzer script folder (`typescript`, `acorn`); without Node the C# side
  still works and the scan reports a warning.

## Run

```bash
dotnet run --project src/Codemap.Web
```

Open the app, paste a path to a repository / `.sln` / project folder, hit **Analyze**
(or `Ctrl+Enter`). Progress streams live over SignalR. **Publish snapshot** persists the scan;
**History** lists snapshots, recent scans, and diffs two snapshots (added/removed/changed nodes
and edges, highlighted on the canvas).

Database schema is created automatically at startup via EF Core migrations. To add migrations:

```bash
dotnet dotnet-ef migrations add <Name> --project src/Codemap.Infrastructure --startup-project src/Codemap.Web --output-dir Persistence/Migrations
```

## Keyboard shortcuts

`?` shows the in-app cheat-sheet. Highlights:

| Shortcut | Action |
|---|---|
| `Ctrl/⌘ + K` | Quick-jump to any node |
| `Ctrl/⌘ + Enter` | Analyze |
| `1` / `2` / `3` | Language filter (All / C# / JS) |
| `F` | Zoom to fit |
| `+` / `-` | Zoom in / out |
| Arrow keys | Nudge selected node (`Shift` = 10 px) |
| `Ctrl/⌘ + F` | Focus the namespace filter |
| `Esc` | Clear selection / close panels |

## How analysis works (details)

- **C#** — `MSBuildWorkspace` loads solutions/projects; `SymbolWalker` (SyntaxWalker +
  SemanticModel) emits nodes, member signatures, and ASP.NET route attributes;
  `CallGraphBuilder` resolves invocations/base lists/member types into
  `Calls`/`Inherits`/`Implements`/`References` edges. Edges point at open generic definitions;
  partial classes collapse into one node; cross-project references resolve within a solution.
- **JS/TS** — a bundled Node script (invoked via `Jering.Javascript.NodeJS`) uses the TypeScript
  Compiler API for `.ts`/`.tsx`/`.jsx` and Acorn for plain `.js`. Modules, classes and top-level
  functions become nodes; imports become `References`; same-module calls become `Calls`,
  cross-module calls degrade to lower-confidence `References`.
- **Cross-language** — `fetch`/`axios`/`$http` call sites with literal (or simple template) URLs
  are matched against `[Route]`/`[Http*]` attributes by normalized route pattern
  (case-insensitive, parameter names ignored) and emit `Invokes` edges; unmatched calls surface
  as scan warnings.

## Tests

```bash
dotnet test
```

Covers `SymbolWalker` node/endpoint extraction, `CallGraphBuilder` edge extraction (inheritance,
implements, calls, references, open generics), cross-language route matching, the dispatcher
(handler resolution, behavior ordering, notification fan-out), topology diffing, cycle detection,
and the keyboard shortcut mapping.

## License

Codemap is released under the [MIT License](LICENSE).
