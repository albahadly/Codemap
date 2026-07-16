using Codemap.Domain;

namespace Codemap.Web.Services;

public enum LanguageFilter { All, CSharp, Js }

public static class LanguageFilterExtensions
{
    public static bool Matches(this LanguageFilter filter, Language language) => filter switch
    {
        LanguageFilter.CSharp => language == Language.CSharp,
        LanguageFilter.Js => language is Language.JavaScript or Language.TypeScript,
        _ => true,
    };
}
