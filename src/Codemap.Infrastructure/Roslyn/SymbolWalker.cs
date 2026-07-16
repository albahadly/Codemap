using Codemap.Application.Abstractions;
using Codemap.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeKind = Codemap.Domain.TypeKind;

namespace Codemap.Infrastructure.Roslyn;

/// <summary>
/// Visits every type declaration in a syntax tree via <see cref="CSharpSyntaxWalker"/> + the tree's
/// <see cref="SemanticModel"/>, emitting one <see cref="CodeNode"/> per class/interface/enum/struct
/// (deduplicated by id, so partial classes collapse onto a single node) plus the ASP.NET HTTP
/// endpoints declared on controller actions (consumed later by the cross-language resolver).
/// </summary>
public sealed class SymbolWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly Dictionary<string, CodeNode> _nodes;
    private readonly List<HttpEndpoint> _endpoints;
    private readonly string _rootPath;
    private readonly CancellationToken _ct;

    private SymbolWalker(
        SemanticModel semanticModel,
        Dictionary<string, CodeNode> nodes,
        List<HttpEndpoint> endpoints,
        string rootPath,
        CancellationToken ct)
    {
        _semanticModel = semanticModel;
        _nodes = nodes;
        _endpoints = endpoints;
        _rootPath = rootPath;
        _ct = ct;
    }

    /// <summary>Walks every tree of the compilation. Results accumulate into the passed collections.</summary>
    public static void Walk(
        Compilation compilation,
        Dictionary<string, CodeNode> nodes,
        List<HttpEndpoint> endpoints,
        string rootPath,
        CancellationToken ct,
        Action<string>? onFile = null)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            onFile?.Invoke(tree.FilePath);
            WalkTree(compilation, tree, nodes, endpoints, rootPath, ct);
        }
    }

    /// <summary>Walks a single tree, letting callers interleave async progress reporting between files.</summary>
    public static void WalkTree(
        Compilation compilation,
        SyntaxTree tree,
        Dictionary<string, CodeNode> nodes,
        List<HttpEndpoint> endpoints,
        string rootPath,
        CancellationToken ct)
    {
        var walker = new SymbolWalker(compilation.GetSemanticModel(tree), nodes, endpoints, rootPath, ct);
        walker.Visit(tree.GetRoot(ct));
    }

    public override void VisitCompilationUnit(CompilationUnitSyntax node)
    {
        CaptureTopLevelProgram(node);
        base.VisitCompilationUnit(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) { Capture(node); base.VisitClassDeclaration(node); }
    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) { Capture(node); base.VisitRecordDeclaration(node); }
    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) { Capture(node); base.VisitInterfaceDeclaration(node); }
    public override void VisitStructDeclaration(StructDeclarationSyntax node) { Capture(node); base.VisitStructDeclaration(node); }
    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) { Capture(node); base.VisitEnumDeclaration(node); }

    /// <summary>
    /// Files with top-level statements (e.g. Program.cs) declare no type syntax, so the regular
    /// type-declaration visits never see them — capture the compiler-synthesized Program class here,
    /// with a synthetic member row for the entry point plus any user-declared public members
    /// (from a `partial class Program`, if present).
    /// </summary>
    private void CaptureTopLevelProgram(CompilationUnitSyntax root)
    {
        if (!root.Members.OfType<GlobalStatementSyntax>().Any()) return;
        if (_semanticModel.Compilation.GetEntryPoint(_ct) is not { } entryPoint) return;

        var symbol = entryPoint.ContainingType;
        var id = SymbolIds.IdFor(symbol);
        if (_nodes.ContainsKey(id)) return;

        var members = new List<MemberSignature>
        {
            new("Main(top-level statements)", SymbolIds.ShortType(entryPoint.ReturnType), true),
        };
        members.AddRange(BuildMembers(symbol));

        _nodes[id] = new CodeNode(
            id,
            symbol.Name,
            TypeKind.Class,
            Language.CSharp,
            SymbolIds.NamespaceFor(symbol),
            members,
            Relativize(root.SyntaxTree.FilePath),
            1);
    }

    private void Capture(BaseTypeDeclarationSyntax declaration)
    {
        _ct.ThrowIfCancellationRequested();
        if (_semanticModel.GetDeclaredSymbol(declaration, _ct) is not INamedTypeSymbol symbol) return;
        if (symbol.IsImplicitlyDeclared) return;

        var id = SymbolIds.IdFor(symbol);
        if (!_nodes.ContainsKey(id))
        {
            var kind = MapKind(symbol);
            if (kind is null) return;

            _nodes[id] = new CodeNode(
                id,
                SymbolIds.DisplayNameFor(symbol),
                kind.Value,
                Language.CSharp,
                SymbolIds.NamespaceFor(symbol),
                BuildMembers(symbol),
                Relativize(declaration.SyntaxTree.FilePath),
                declaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

            CollectHttpEndpoints(symbol, id);
        }
    }

    private static TypeKind? MapKind(INamedTypeSymbol symbol) => symbol.TypeKind switch
    {
        Microsoft.CodeAnalysis.TypeKind.Class => TypeKind.Class,
        Microsoft.CodeAnalysis.TypeKind.Interface => TypeKind.Interface,
        Microsoft.CodeAnalysis.TypeKind.Enum => TypeKind.Enum,
        Microsoft.CodeAnalysis.TypeKind.Struct => TypeKind.Struct,
        _ => null, // delegates et al. are out of scope for v1
    };

    private static IReadOnlyList<MemberSignature> BuildMembers(INamedTypeSymbol symbol)
    {
        var members = new List<MemberSignature>();
        foreach (var member in symbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            if (member.DeclaredAccessibility != Accessibility.Public) continue;

            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method:
                    members.Add(new MemberSignature(
                        SymbolIds.MemberSignatureFor(method),
                        method.MethodKind == MethodKind.Constructor ? "ctor" : SymbolIds.ShortType(method.ReturnType),
                        method.IsStatic));
                    break;
                case IPropertySymbol property:
                    members.Add(new MemberSignature(property.Name, SymbolIds.ShortType(property.Type), property.IsStatic));
                    break;
                case IFieldSymbol field:
                    members.Add(new MemberSignature(
                        field.Name,
                        symbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Enum ? symbol.Name : SymbolIds.ShortType(field.Type),
                        field.IsStatic));
                    break;
                case IEventSymbol @event:
                    members.Add(new MemberSignature(@event.Name, SymbolIds.ShortType(@event.Type), @event.IsStatic));
                    break;
            }
        }
        return members;
    }

    private void CollectHttpEndpoints(INamedTypeSymbol symbol, string nodeId)
    {
        if (symbol.TypeKind != Microsoft.CodeAnalysis.TypeKind.Class) return;
        if (!LooksLikeController(symbol)) return;

        var classRoute = symbol.GetAttributes()
            .Where(a => a.AttributeClass?.Name is "RouteAttribute")
            .Select(FirstStringArgument)
            .FirstOrDefault(r => r is not null) ?? string.Empty;

        var controllerName = symbol.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? symbol.Name[..^"Controller".Length]
            : symbol.Name;

        foreach (var method in symbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (method.MethodKind != MethodKind.Ordinary || method.DeclaredAccessibility != Accessibility.Public) continue;

            foreach (var attribute in method.GetAttributes())
            {
                var httpMethod = attribute.AttributeClass?.Name switch
                {
                    "HttpGetAttribute" => "GET",
                    "HttpPostAttribute" => "POST",
                    "HttpPutAttribute" => "PUT",
                    "HttpDeleteAttribute" => "DELETE",
                    "HttpPatchAttribute" => "PATCH",
                    "HttpHeadAttribute" => "HEAD",
                    _ => null,
                };
                if (httpMethod is null) continue;

                var methodRoute = FirstStringArgument(attribute) ?? string.Empty;
                var template = CombineRoutes(classRoute, methodRoute)
                    .Replace("[controller]", controllerName, StringComparison.OrdinalIgnoreCase)
                    .Replace("[action]", method.Name, StringComparison.OrdinalIgnoreCase);

                _endpoints.Add(new HttpEndpoint(nodeId, httpMethod, template));
            }
        }
    }

    private static bool LooksLikeController(INamedTypeSymbol symbol)
    {
        if (symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "ApiControllerAttribute")) return true;
        for (var baseType = symbol.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.Name is "Controller" or "ControllerBase") return true;
        }
        return symbol.Name.EndsWith("Controller", StringComparison.Ordinal)
               && symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "RouteAttribute");
    }

    private static string? FirstStringArgument(AttributeData attribute) =>
        attribute.ConstructorArguments.FirstOrDefault(a => a.Value is string).Value as string;

    private static string CombineRoutes(string prefix, string route)
    {
        if (route.StartsWith('/') || route.StartsWith("~/", StringComparison.Ordinal))
            return route.TrimStart('~');
        if (string.IsNullOrEmpty(prefix)) return route;
        if (string.IsNullOrEmpty(route)) return prefix;
        return $"{prefix.TrimEnd('/')}/{route.TrimStart('/')}";
    }

    private string Relativize(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;
        try
        {
            return Path.GetRelativePath(_rootPath, filePath);
        }
        catch (ArgumentException)
        {
            return filePath;
        }
    }
}
