using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Codemap.Tests.Roslyn;

internal static class RoslynTestHelper
{
    /// <summary>In-memory compilation over fixture sources — Roslyn end to end, no MSBuild required.</summary>
    public static CSharpCompilation Compile(params string[] sources) =>
        Compile(OutputKind.DynamicallyLinkedLibrary, sources);

    /// <summary>Console output kind is needed for top-level-statement fixtures (entry point resolution).</summary>
    public static CSharpCompilation Compile(OutputKind outputKind, params string[] sources)
    {
        var trees = sources
            .Select((source, i) => CSharpSyntaxTree.ParseText(source, path: $"Fixture{i}.cs"))
            .ToArray();

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
            {
                "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll",
                "System.Linq.dll", "netstandard.dll",
            }
            .Select(name => Path.Combine(runtimeDir, name))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        return CSharpCompilation.Create(
            "Fixture",
            trees,
            references,
            new CSharpCompilationOptions(outputKind));
    }
}
