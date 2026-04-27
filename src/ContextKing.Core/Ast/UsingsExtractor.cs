using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts using directives (C#) and import statements (TypeScript/TSX) from source files.
/// Always reads live from disk; never uses cached data.
/// </summary>
public static class UsingsExtractor
{
    /// <summary>
    /// Extracts all using/import directives from a C# or TypeScript file.
    /// Returns one string per directive in source order.
    /// </summary>
    public static IReadOnlyList<string> Extract(string filePath)
    {
        var source = File.ReadAllText(filePath);

        return SupportedLanguages.IsTypeScript(filePath)
            ? ExtractTypeScriptImports(source)
            : ExtractCSharpUsings(source, filePath);
    }

    private static IReadOnlyList<string> ExtractCSharpUsings(string source, string filePath)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root = tree.GetRoot();

        return root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.ToString().Trim())
            .ToList();
    }

    private static IReadOnlyList<string> ExtractTypeScriptImports(string source)
    {
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var results = new List<string>();

        CollectImports(root, source, results);
        return results;
    }

    private static void CollectImports(TSNode node, string source, List<string> results)
    {
        var nodeType = node.type();

        if (nodeType == "import_statement")
        {
            var start = (int)node.start_offset();
            var end   = (int)node.end_offset();
            results.Add(source[start..end].Trim());
            return; // don't recurse into import body
        }

        // Only scan at the top-level program scope — imports are never nested inside functions.
        // Stop recursing when we hit a class, function, or block to keep this fast.
        if (nodeType is "class_declaration" or "function_declaration" or "statement_block"
                     or "method_definition" or "arrow_function")
            return;

        for (uint i = 0; i < node.child_count(); i++)
            CollectImports(node.child(i), source, results);
    }
}
