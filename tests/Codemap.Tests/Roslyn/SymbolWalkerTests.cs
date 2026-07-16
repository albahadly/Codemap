using Codemap.Application.Abstractions;
using Codemap.Domain;
using Codemap.Infrastructure.Roslyn;

namespace Codemap.Tests.Roslyn;

public class SymbolWalkerTests
{
    private static (Dictionary<string, CodeNode> Nodes, List<HttpEndpoint> Endpoints) Walk(params string[] sources) =>
        Walk(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary, sources);

    private static (Dictionary<string, CodeNode> Nodes, List<HttpEndpoint> Endpoints) Walk(
        Microsoft.CodeAnalysis.OutputKind outputKind, params string[] sources)
    {
        var compilation = RoslynTestHelper.Compile(outputKind, sources);
        var nodes = new Dictionary<string, CodeNode>(StringComparer.Ordinal);
        var endpoints = new List<HttpEndpoint>();
        SymbolWalker.Walk(compilation, nodes, endpoints, Directory.GetCurrentDirectory(), CancellationToken.None);
        return (nodes, endpoints);
    }

    [Fact]
    public void Extracts_class_interface_enum_and_struct_nodes()
    {
        var (nodes, _) = Walk("""
            namespace Fixture.App;

            public interface IWidget { void Render(); }
            public class Widget : IWidget { public void Render() { } }
            public enum Color { Red, Green }
            public struct Point { public int X; public int Y; }
            """);

        Assert.Equal(TypeKind.Interface, nodes["Fixture.App.IWidget"].Kind);
        Assert.Equal(TypeKind.Class, nodes["Fixture.App.Widget"].Kind);
        Assert.Equal(TypeKind.Enum, nodes["Fixture.App.Color"].Kind);
        Assert.Equal(TypeKind.Struct, nodes["Fixture.App.Point"].Kind);
        Assert.All(nodes.Values, n => Assert.Equal(Language.CSharp, n.Language));
        Assert.Equal("Fixture.App", nodes["Fixture.App.Widget"].Namespace);
    }

    [Fact]
    public void Collects_public_member_signatures_with_static_flag()
    {
        var (nodes, _) = Walk("""
            namespace Fixture.App;

            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public static Calculator Create() => new();
                public string Name { get; set; } = "";
                private void Hidden() { }
            }
            """);

        var members = nodes["Fixture.App.Calculator"].Members;
        Assert.Contains(members, m => m.Signature == "Add(int a, int b)" && m.ReturnOrType == "int" && !m.IsStatic);
        Assert.Contains(members, m => m.Signature == "Create()" && m.IsStatic);
        Assert.Contains(members, m => m.Signature == "Name" && m.ReturnOrType == "string");
        Assert.DoesNotContain(members, m => m.Signature.Contains("Hidden"));
    }

    [Fact]
    public void Partial_classes_collapse_onto_a_single_node()
    {
        var (nodes, _) = Walk(
            "namespace Fixture.App; public partial class Split { public void A() { } }",
            "namespace Fixture.App; public partial class Split { public void B() { } }");

        var node = Assert.Single(nodes.Values, n => n.DisplayName == "Split");
        // both parts' members are visible because the symbol spans all partial declarations
        Assert.Contains(node.Members, m => m.Signature == "A()");
        Assert.Contains(node.Members, m => m.Signature == "B()");
    }

    [Fact]
    public void Generic_types_are_identified_by_their_open_definition()
    {
        var (nodes, _) = Walk("namespace Fixture.App; public class Repository<T> { public T? Find(int id) => default; }");
        Assert.Contains("Fixture.App.Repository<T>", nodes.Keys);
        Assert.Equal("Repository<T>", nodes["Fixture.App.Repository<T>"].DisplayName);
    }

    [Fact]
    public void Top_level_statements_produce_a_program_node()
    {
        var (nodes, _) = Walk(Microsoft.CodeAnalysis.OutputKind.ConsoleApplication, """
            var greeting = "hello";
            System.Console.WriteLine(greeting);
            """);

        var program = Assert.Single(nodes.Values, n => n.DisplayName == "Program");
        Assert.Equal(TypeKind.Class, program.Kind);
        Assert.Contains(program.Members, m => m.Signature == "Main(top-level statements)");
    }

    [Fact]
    public void Extracts_http_endpoints_from_controller_route_attributes()
    {
        var (nodes, endpoints) = Walk("""
            namespace Microsoft.AspNetCore.Mvc
            {
                public class ControllerBase { }
                public class RouteAttribute : System.Attribute { public RouteAttribute(string template) { } }
                public class HttpGetAttribute : System.Attribute { public HttpGetAttribute() { } public HttpGetAttribute(string template) { } }
                public class HttpPostAttribute : System.Attribute { public HttpPostAttribute() { } public HttpPostAttribute(string template) { } }
            }

            namespace Fixture.Api
            {
                using Microsoft.AspNetCore.Mvc;

                [Route("api/[controller]")]
                public class ItemsController : ControllerBase
                {
                    [HttpGet("{id}")] public string GetOne(int id) => "";
                    [HttpPost] public void Create() { }
                }
            }
            """);

        Assert.Contains(endpoints, e =>
            e is { HttpMethod: "GET", RouteTemplate: "api/Items/{id}" } &&
            e.NodeId == "Fixture.Api.ItemsController");
        Assert.Contains(endpoints, e => e is { HttpMethod: "POST", RouteTemplate: "api/Items" });
        Assert.True(nodes.ContainsKey("Fixture.Api.ItemsController"));
    }
}
