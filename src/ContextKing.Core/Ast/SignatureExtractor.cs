using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts method, constructor, and property signatures from C# source files
/// using Roslyn's CSharpSyntaxTree.ParseText — no solution or compilation required.
/// Always reads live from disk; never uses cached data.
/// </summary>
public static class SignatureExtractor
{
    /// <summary>
    /// Extracts all public-surface signatures from each file in <paramref name="filePaths"/>
    /// and writes them to <paramref name="writer"/> in the format:
    ///   filepath:line\tcontainingType\tmemberName\tsignature
    /// Parse errors for a file are reported to <paramref name="errorWriter"/> and that file is skipped.
    /// </summary>
    public static void Extract(
        IEnumerable<string> filePaths,
        TextWriter writer,
        TextWriter? errorWriter = null)
    {
        errorWriter ??= Console.Error;

        foreach (var path in filePaths)
        {
            try
            {
                ExtractFromFile(path, writer);
            }
            catch (Exception ex)
            {
                errorWriter.WriteLine($"[ck-signatures] WARN: skipping '{path}': {ex.Message}");
            }
        }
    }

    private static void ExtractFromFile(string path, TextWriter writer)
    {
        var source = File.ReadAllText(path);
        var tree   = CSharpSyntaxTree.ParseText(source, path: path);
        var root   = tree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax m:
                    Emit(writer, path, m, GetContainingTypeName(m),
                        m.Identifier.Text, BuildMethodSignature(m));
                    break;

                case ConstructorDeclarationSyntax c:
                    Emit(writer, path, c, GetContainingTypeName(c),
                        c.Identifier.Text, BuildConstructorSignature(c));
                    break;

                case PropertyDeclarationSyntax p:
                    Emit(writer, path, p, GetContainingTypeName(p),
                        p.Identifier.Text, BuildPropertySignature(p));
                    break;
            }
        }
    }

    private static void Emit(
        TextWriter writer, string path, SyntaxNode node,
        string containingType, string memberName, string signature)
    {
        var lineSpan       = node.GetLocation().GetLineSpan();
        var line           = lineSpan.StartLinePosition.Line + 1; // 1-based
        var normalizedPath = path.Replace('\\', '/');
        writer.WriteLine($"{normalizedPath}:{line}\t{containingType}\t{memberName}\t{signature}");
    }

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var parts = new List<string>();
        foreach (var ancestor in node.Ancestors().OfType<TypeDeclarationSyntax>())
            parts.Insert(0, ancestor.Identifier.Text);
        return parts.Count > 0 ? string.Join('.', parts) : "<global>";
    }

    private static string BuildMethodSignature(MethodDeclarationSyntax m)
    {
        var mods       = m.Modifiers.ToString();
        var returnType = m.ReturnType.ToString();
        var name       = m.Identifier.Text;
        var typeParams = m.TypeParameterList?.ToString() ?? string.Empty;
        var parameters = m.ParameterList.ToString();
        return $"{mods} {returnType} {name}{typeParams}{parameters}".Trim();
    }

    private static string BuildConstructorSignature(ConstructorDeclarationSyntax c)
    {
        var mods       = c.Modifiers.ToString();
        var name       = c.Identifier.Text;
        var parameters = c.ParameterList.ToString();
        return $"{mods} {name}{parameters}".Trim();
    }

    private static string BuildPropertySignature(PropertyDeclarationSyntax p)
    {
        var mods       = p.Modifiers.ToString();
        var type       = p.Type.ToString();
        var name       = p.Identifier.Text;
        var accessors  = p.AccessorList is not null
            ? " { " + string.Join(" ", p.AccessorList.Accessors.Select(a =>
                a.Modifiers.ToString() is { Length: > 0 } m ? $"{m} {a.Keyword};" : $"{a.Keyword};")) + " }"
            : string.Empty;
        return $"{mods} {type} {name}{accessors}".Trim();
    }
}
