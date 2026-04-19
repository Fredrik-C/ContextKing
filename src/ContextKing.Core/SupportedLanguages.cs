namespace ContextKing.Core;

/// <summary>
/// Canonical policy for which source files <c>ck</c> recognises.
/// All file-extension predicates live here so adding a new language is a one-file change.
/// </summary>
public static class SupportedLanguages
{
    /// <summary>Git pathspec covering every supported source extension.</summary>
    public const string GitPathspec = "-- *.cs *.ts *.tsx";

    /// <summary><c>true</c> when <paramref name="path"/> is a C# source file.</summary>
    public static bool IsCSharp(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is a TypeScript or TSX source file.</summary>
    public static bool IsTypeScript(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>true</c> when <paramref name="path"/> is any supported source file.</summary>
    public static bool IsSupported(string path) =>
        IsCSharp(path) || IsTypeScript(path);
}
