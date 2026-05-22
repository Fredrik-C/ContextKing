namespace ContextKing.Core;

/// <summary>
/// Canonical policy for which source files <c>ck</c> recognises.
/// All file-extension predicates live here so adding a new language is a one-file change.
/// Delegates to <see cref="Ast.LanguageRegistry"/> for extension-to-extractor mapping.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>Git pathspec covering every supported source extension (built from LanguageRegistry).</summary>
    public static string GitPathspec => Ast.LanguageRegistry.BuildGitPathspec();

    /// <summary><c>true</c> when <paramref name="path"/> is a C# source file.</summary>
    public static bool IsCSharp(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is a TypeScript or TSX source file.</summary>
    public static bool IsTypeScript(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is a Kotlin source file.</summary>
    public static bool IsKotlin(string path) =>
        path.EndsWith(".kt", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".kts", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is a Python source file.</summary>
    public static bool IsPython(string path) =>
        path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is any supported source file.</summary>
    public static bool IsSupported(string path) =>
        Ast.LanguageRegistry.IsSupported(path);
}
