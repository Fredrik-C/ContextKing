using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast.TypeScript;

/// <summary>
/// Extracts the names of all exported functions and public methods from a TypeScript source file
/// using tree-sitter. Lightweight alternative to <see cref="TsSignatureExtractor"/> — returns
/// only method/function names for use as lexical keywords in the source-map index.
/// </summary>
public static class TsPublicMethodNameExtractor
{
    /// <summary>
    /// Returns the distinct names of all exported functions and public class methods
    /// declared in <paramref name="sourceText"/>. Only explicitly exported or public
    /// members are included.
    /// </summary>
    public static IReadOnlyList<string> Extract(string sourceText)
    {
        using var parser = new Parser();
        var tree = parser.ParseString(sourceText);
        var root = tree.root_node();

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        CollectNames(root, sourceText, isExported: false, names, seen);

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

    private static void CollectNames(
        TSNode node, string source, bool isExported,
        List<string> names, HashSet<string> seen)
    {
        var type = node.type();

        // export_statement wraps declarations — mark children as exported
        if (type == "export_statement")
        {
            for (uint i = 0; i < node.child_count(); i++)
                CollectNames(node.child(i), source, isExported: true, names, seen);
            return;
        }

        // Exported function
        if (type == "function_declaration" && isExported)
        {
            AddName(node, source, names, seen);
            return;
        }

        // Class methods — only include if they have `public` modifier or no access modifier
        // (TypeScript default visibility is public)
        if (type is "method_definition")
        {
            if (!HasPrivateOrProtected(node, source))
                AddName(node, source, names, seen);
            return;
        }

        // Interface method signatures are always public
        if (type is "method_signature")
        {
            AddName(node, source, names, seen);
            return;
        }

        // Recurse into classes/interfaces/modules
        for (uint i = 0; i < node.child_count(); i++)
            CollectNames(node.child(i), source, isExported, names, seen);
    }

    private static void AddName(TSNode node, string source, List<string> names, HashSet<string> seen)
    {
        var nameNode = node.child_by_field_name("name");
        if (nameNode.is_null()) return;

        var name = nameNode.text(source);
        if (name is "constructor") return; // skip constructors
        if (seen.Add(name))
            names.Add(name);
    }

    private static bool HasPrivateOrProtected(TSNode node, string source)
    {
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            if (child.type() == "accessibility_modifier")
            {
                var mod = child.text(source);
                return mod is "private" or "protected";
            }
        }
        return false;
    }
}
