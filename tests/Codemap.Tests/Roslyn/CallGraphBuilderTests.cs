using Codemap.Application.Abstractions;
using Codemap.Domain;
using Codemap.Infrastructure.Roslyn;

namespace Codemap.Tests.Roslyn;

public class CallGraphBuilderTests
{
    private static IReadOnlyList<CodeEdge> BuildEdges(params string[] sources) =>
        BuildEdges(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, sources);

    private static IReadOnlyList<CodeEdge> BuildEdges(Microsoft.CodeAnalysis.OutputKind outputKind, params string[] sources)
    {
        var compilation = RoslynTestHelper.Compile(outputKind, sources);

        var nodes = new Dictionary<string, CodeNode>(StringComparer.Ordinal);
        var endpoints = new List<HttpEndpoint>();
        SymbolWalker.Walk(compilation, nodes, endpoints, Directory.GetCurrentDirectory(), CancellationToken.None);

        var raw = new CallGraphBuilder().BuildEdges(compilation, CancellationToken.None);
        return CallGraphBuilder.Consolidate(raw, nodes.Keys.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Base_class_produces_inherits_edge()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Animal { }
            public class Dog : Animal { }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.Inherits, FromId: "Fixture.Dog", ToId: "Fixture.Animal" });
    }

    [Fact]
    public void Interface_implementation_produces_implements_edge()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public interface IRunner { void Run(); }
            public class Sprinter : IRunner { public void Run() { } }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.Implements, FromId: "Fixture.Sprinter", ToId: "Fixture.IRunner" });
    }

    [Fact]
    public void Method_invocation_produces_calls_edge_resolved_via_semantic_model()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Service { public int Compute() => 42; }
            public class Consumer
            {
                public int Use(Service service) => service.Compute();
            }
            """);

        var call = Assert.Single(edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "Fixture.Consumer", ToId: "Fixture.Service" });
        Assert.Equal("Compute", call.Detail);
    }

    [Fact]
    public void Object_creation_counts_as_a_call()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Widget { }
            public class Factory { public Widget Make() => new Widget(); }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "Fixture.Factory", ToId: "Fixture.Widget" });
    }

    [Fact]
    public void Member_types_produce_references_edges()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Order { }
            public class OrderBook
            {
                public Order? Latest { get; set; }
            }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.References, FromId: "Fixture.OrderBook", ToId: "Fixture.Order" });
    }

    [Fact]
    public void Edges_point_to_the_open_generic_definition()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Repository<T> { public void Save(T entity) { } }
            public class Customer { }
            public class CustomerStore
            {
                private readonly Repository<Customer> _repo = new Repository<Customer>();
                public void Persist(Customer c) => _repo.Save(c);
            }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "Fixture.CustomerStore", ToId: "Fixture.Repository<T>" });
        // the closed generic's type argument still yields a References edge to the argument type
        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.References, FromId: "Fixture.CustomerStore", ToId: "Fixture.Customer" });
        Assert.DoesNotContain(edges, e => e.ToId.Contains("Repository<Customer>"));
    }

    [Fact]
    public void Top_level_statement_calls_are_attributed_to_program()
    {
        var edges = BuildEdges(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication, """
            var runner = new Fixture.Runner();
            runner.Go();

            namespace Fixture
            {
                public class Runner { public void Go() { } }
            }
            """);

        Assert.Contains(edges, e =>
            e is { Kind: EdgeKind.Calls, FromId: "Program", ToId: "Fixture.Runner" });
    }

    [Fact]
    public void Edges_to_types_outside_the_node_set_are_dropped()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Lister
            {
                public System.Collections.Generic.List<int> Numbers { get; set; } = new();
            }
            """);

        Assert.DoesNotContain(edges, e => e.ToId.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_call_edges_consolidate_with_aggregated_detail()
    {
        var edges = BuildEdges("""
            namespace Fixture;
            public class Target
            {
                public void First() { }
                public void Second() { }
            }
            public class Caller
            {
                public void Go(Target t) { t.First(); t.Second(); t.First(); }
            }
            """);

        var call = Assert.Single(edges, e => e.Kind == EdgeKind.Calls && e.FromId == "Fixture.Caller");
        Assert.NotNull(call.Detail);
        Assert.Contains("+1 more", call.Detail);
    }
}
