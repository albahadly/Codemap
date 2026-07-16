using Codemap.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Codemap.Infrastructure.Roslyn;

/// <summary>
/// Builds typed edges for a compilation:
///  - base class list (semantic)          → Inherits (interface-extends-interface also counts as Inherits)
///  - implemented interfaces (semantic)   → Implements
///  - invocation + object-creation exprs  → Calls (resolved via SemanticModel.GetSymbolInfo; `new T()` is a
///                                          constructor call, so it lands here rather than References)
///  - field/property/parameter/return types → References (type arguments unwrapped, e.g. List&lt;Order&gt; → Order)
/// Edges point at open generic definitions. Raw edges may reference types outside the scanned source —
/// the analyzer filters them against the final node set, and dedupes/aggregates call details.
/// </summary>
public sealed class CallGraphBuilder
{
    public IReadOnlyList<CodeEdge> BuildEdges(Compilation compilation, CancellationToken ct, Action<string>? onFile = null)
    {
        var edges = new List<CodeEdge>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            onFile?.Invoke(tree.FilePath);
            BuildEdgesForTree(compilation, tree, edges, ct);
        }
        return edges;
    }

    /// <summary>Builds raw edges for one tree, letting callers interleave async progress between files.</summary>
    public static void BuildEdgesForTree(Compilation compilation, SyntaxTree tree, List<CodeEdge> edges, CancellationToken ct)
    {
        var walker = new EdgeWalker(compilation.GetSemanticModel(tree), edges, ct);
        walker.Visit(tree.GetRoot(ct));
    }

    /// <summary>Dedupes edges and aggregates Calls details ("MethodA +2 more") after node filtering.</summary>
    public static IReadOnlyList<CodeEdge> Consolidate(IEnumerable<CodeEdge> edges, ISet<string> knownNodeIds)
    {
        var kept = edges
            .Where(e => !e.FromId.Equals(e.ToId, StringComparison.Ordinal))
            .Where(e => knownNodeIds.Contains(e.FromId) && knownNodeIds.Contains(e.ToId))
            .ToList();

        var result = new List<CodeEdge>();
        foreach (var group in kept.GroupBy(e => (e.FromId, e.ToId, e.Kind)))
        {
            var details = group.Select(e => e.Detail).Where(d => !string.IsNullOrEmpty(d)).Distinct().ToList();
            var detail = details.Count switch
            {
                0 => null,
                1 => details[0],
                _ => $"{details[0]} +{details.Count - 1} more",
            };
            result.Add(new CodeEdge(group.Key.FromId, group.Key.ToId, group.Key.Kind, detail));
        }
        return result;
    }

    private sealed class EdgeWalker(SemanticModel semanticModel, List<CodeEdge> edges, CancellationToken ct)
        : CSharpSyntaxWalker
    {
        private readonly Stack<string> _typeStack = new();

        private string? CurrentTypeId => _typeStack.Count > 0 ? _typeStack.Peek() : null;

        public override void VisitCompilationUnit(CompilationUnitSyntax node)
        {
            ct.ThrowIfCancellationRequested();
            // Top-level statements: attribute their calls/creations to the synthesized Program class
            // (declared types in the same file still push their own id on top of the stack).
            if (node.Members.OfType<GlobalStatementSyntax>().Any()
                && semanticModel.Compilation.GetEntryPoint(ct)?.ContainingType is { } program)
            {
                _typeStack.Push(SymbolIds.IdFor(program));
                base.VisitCompilationUnit(node);
                _typeStack.Pop();
            }
            else
            {
                base.VisitCompilationUnit(node);
            }
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node, () => base.VisitClassDeclaration(node));
        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node, () => base.VisitRecordDeclaration(node));
        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node, () => base.VisitInterfaceDeclaration(node));
        public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node, () => base.VisitStructDeclaration(node));

        private void VisitType(TypeDeclarationSyntax node, Action visitChildren)
        {
            ct.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(node, ct) is not INamedTypeSymbol symbol)
            {
                visitChildren();
                return;
            }

            var fromId = SymbolIds.IdFor(symbol);
            _typeStack.Push(fromId);

            // Base list — emitted from the symbol, so partial classes repeat it; consolidation dedupes.
            if (symbol.BaseType is { SpecialType: SpecialType.None } baseType)
                Add(fromId, baseType, EdgeKind.Inherits);
            foreach (var iface in symbol.Interfaces)
                Add(fromId, iface, symbol.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface ? EdgeKind.Inherits : EdgeKind.Implements);

            // Member surface types → References (only for members declared in this syntax part,
            // via the symbol; duplicates across partials collapse in consolidation).
            foreach (var member in symbol.GetMembers())
            {
                switch (member)
                {
                    case IFieldSymbol field when !field.IsImplicitlyDeclared:
                        AddTypeReference(fromId, field.Type);
                        break;
                    case IPropertySymbol property when !property.IsImplicitlyDeclared:
                        AddTypeReference(fromId, property.Type);
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } method when !method.IsImplicitlyDeclared:
                        AddTypeReference(fromId, method.ReturnType);
                        foreach (var parameter in method.Parameters)
                            AddTypeReference(fromId, parameter.Type);
                        break;
                }
            }

            visitChildren();
            _typeStack.Pop();
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            ct.ThrowIfCancellationRequested();
            if (CurrentTypeId is { } fromId && ResolveMethod(node) is { } method && method.ContainingType is { } target)
                Add(fromId, target, EdgeKind.Calls, method.Name);
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            ct.ThrowIfCancellationRequested();
            if (CurrentTypeId is { } fromId && ResolveMethod(node) is { } ctor && ctor.ContainingType is { } target)
                Add(fromId, target, EdgeKind.Calls, "new");
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            ct.ThrowIfCancellationRequested();
            if (CurrentTypeId is { } fromId && ResolveMethod(node) is { } ctor && ctor.ContainingType is { } target)
                Add(fromId, target, EdgeKind.Calls, "new");
            base.VisitImplicitObjectCreationExpression(node);
        }

        private IMethodSymbol? ResolveMethod(SyntaxNode node)
        {
            var info = semanticModel.GetSymbolInfo(node, ct);
            return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        }

        private void AddTypeReference(string fromId, ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    AddTypeReference(fromId, array.ElementType);
                    break;
                case INamedTypeSymbol named:
                    Add(fromId, named, EdgeKind.References);
                    foreach (var argument in named.TypeArguments)
                        AddTypeReference(fromId, argument);
                    break;
            }
        }

        private void Add(string fromId, INamedTypeSymbol target, EdgeKind kind, string? detail = null)
        {
            if (target.IsAnonymousType || target.SpecialType != SpecialType.None) return;
            edges.Add(new CodeEdge(fromId, SymbolIds.IdFor(target), kind, detail));
        }
    }
}
