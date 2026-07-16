namespace Codemap.Domain;

public sealed record CodeNode(
    string Id,                 // fully qualified name or file-relative module path
    string DisplayName,
    TypeKind Kind,
    Language Language,
    string Namespace,
    IReadOnlyList<MemberSignature> Members,
    string SourceFile,
    int LineNumber);
