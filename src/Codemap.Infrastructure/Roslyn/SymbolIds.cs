using Microsoft.CodeAnalysis;

namespace Codemap.Infrastructure.Roslyn;

/// <summary>
/// Canonical node identity for C# symbols: fully qualified metadata-style name of the
/// <em>open generic definition</em> (spec: edges point to the open generic, so
/// <c>Repository&lt;Order&gt;</c> and <c>Repository&lt;Customer&gt;</c> collapse onto <c>Repository&lt;T&gt;</c>).
/// The same format is used across compilations so partial classes and cross-project references
/// resolve to a single node.
/// </summary>
internal static class SymbolIds
{
    private static readonly SymbolDisplayFormat IdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly SymbolDisplayFormat DisplayNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly SymbolDisplayFormat ShortTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat MemberFormat = new(
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string IdFor(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString(IdFormat);

    public static string DisplayNameFor(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString(DisplayNameFormat);

    public static string NamespaceFor(INamedTypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : string.Empty;

    public static string ShortType(ITypeSymbol type) => type.ToDisplayString(ShortTypeFormat);

    public static string MemberSignatureFor(ISymbol member) => member.ToDisplayString(MemberFormat);
}
