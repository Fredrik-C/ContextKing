using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts the names of all public methods from a C# source file using Roslyn.
/// Lightweight alternative to <see cref="SignatureExtractor"/> — returns only method
/// names (no return types, parameters, or modifiers) for use as lexical keywords
/// in the source-map index.
/// </summary>
public static class PublicMethodNameExtractor
{
    /// <summary>
    /// Returns the distinct names of all public methods declared in <paramref name="sourceText"/>.
    /// Only explicit <c>public</c> methods are included (not properties, constructors, or
    /// interface members that are implicitly public).
    /// </summary>
    public static IReadOnlyList<string> Extract(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                continue;

            var name = method.Identifier.Text;
            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Reads the file at <paramref name="filePath"/> and extracts public method names.
    /// Returns an empty list if the file cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromFile(string filePath)
    {
        try
        {
            var source = File.ReadAllText(filePath);
            return Extract(source);
        }
        catch
        {
            return [];
        }
    }
}
