using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;
using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts using directives (C#) and import statements (TypeScript, Kotlin, Python) from source files.
/// Always reads live from disk; never uses cached data.
/// </summary>
public static class UsingsExtractor
{
    /// <summary>
    /// Extracts all using/import directives from a C#, TypeScript, Kotlin, or Python file.
    /// Returns one string per directive in source order.
    /// </summary>
    public static IReadOnlyList<string> Extract(string filePath)
    {
        var source = File.ReadAllText(filePath);

        if (SupportedLanguages.IsTypeScript(filePath))
            return ExtractTypeScriptImports(source);

        if (SupportedLanguages.IsKotlin(filePath))
            return ExtractTreeSitterImports(source, "kotlin");

        if (SupportedLanguages.IsPython(filePath))
            return ExtractTreeSitterImports(source, "python");

        return ExtractCSharpUsings(source, filePath);
    }

    private static IReadOnlyList<string> ExtractTypeScriptImports(string source)
    {
        using var parser = new TypeScriptParser.Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var results = new List<string>();

        CollectTypeScriptImports(root, source, results);
        return results;
    }

    private static void CollectTypeScriptImports(TSNode node, string source, List<string> results)
    {
        var nodeType = node.type();

        if (nodeType == "import_statement")
        {
            var start = (int)node.start_offset();
            var end = (int)node.end_offset();
            results.Add(source[start..end].Trim());
            return;
        }

        if (nodeType is "class_declaration" or "function_declaration" or "statement_block"
                     or "method_definition" or "arrow_function")
            return;

        for (uint i = 0; i < node.child_count(); i++)
            CollectTypeScriptImports(node.child(i), source, results);
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

    private static IReadOnlyList<string> ExtractTreeSitterImports(string source, string languageName)
    {
        using var parser = TreeSitterLanguagePack.Parser.Default();
        parser.SetLanguage(languageName);
        var tree = parser.Parse(source);
        if (tree is null) return [];

        var root = tree.RootNode();
        if (root is null) return [];

        var results = new List<string>();
        CollectImports(root, source, languageName, results);
        return results;
    }

    private static void CollectImports(Node node, string source, string languageName, List<string> results)
    {
        var kind = node.Kind();

        var importNodeTypes = languageName switch
        {
            "typescript" => (string[])["import_statement"],
            "kotlin" => ["import_header", "import_list"],
            "python" => ["import_statement", "import_from_statement"],
            _ => []
        };

        if (Array.IndexOf(importNodeTypes, kind) >= 0)
        {
            var start = (int)node.StartByte();
            var end = (int)node.EndByte();
            results.Add(source[start..end].Trim());
            return;
        }

        // Only scan at top-level scope — imports are never nested inside functions/classes
        var stopTypes = languageName switch
        {
            "typescript" => (string[])["class_declaration", "function_declaration", "statement_block", "method_definition", "arrow_function"],
            "kotlin" => ["class_declaration", "function_declaration", "lambda_literal", "class_body"],
            "python" => ["function_definition", "class_definition", "block"],
            _ => []
        };

        if (Array.IndexOf(stopTypes, kind) >= 0)
            return;

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is not null)
                CollectImports(child, source, languageName, results);
        }
    }
}
