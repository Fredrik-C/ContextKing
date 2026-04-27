using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts type hierarchy information (class/interface declarations with base types)
/// from C# and TypeScript/TSX files. Always reads live from disk; never uses cached data.
/// </summary>
public static class TypeHierarchyExtractor
{
    public static IReadOnlyList<TypeHierarchyEntry> Extract(string filePath)
    {
        var source = File.ReadAllText(filePath);

        return SupportedLanguages.IsTypeScript(filePath)
            ? ExtractTypeScript(source, filePath)
            : ExtractCSharp(source, filePath);
    }

    // ── C# ────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> ExtractCSharp(string source, string filePath)
    {
        var tree    = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root    = tree.GetRoot();
        var lines   = tree.GetText().Lines;
        var results = new List<TypeHierarchyEntry>();

        foreach (var node in root.DescendantNodes())
        {
            TypeHierarchyEntry? entry = node switch
            {
                ClassDeclarationSyntax c =>
                    MakeCsEntry(filePath, c.Identifier.Text, CsKind(c), c.BaseList, c, lines),
                RecordDeclarationSyntax r =>
                    MakeCsEntry(filePath, r.Identifier.Text, r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record", r.BaseList, r, lines),
                InterfaceDeclarationSyntax i =>
                    MakeCsEntry(filePath, i.Identifier.Text, "interface", i.BaseList, i, lines),
                StructDeclarationSyntax s =>
                    MakeCsEntry(filePath, s.Identifier.Text, "struct", s.BaseList, s, lines),
                EnumDeclarationSyntax e =>
                    MakeCsEnumEntry(filePath, e, lines),
                _ => null
            };

            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    private static string CsKind(ClassDeclarationSyntax c)
        => c.Modifiers.Any(SyntaxKind.AbstractKeyword) ? "abstract class" : "class";

    private static TypeHierarchyEntry MakeCsEntry(
        string filePath, string name, string kind,
        BaseListSyntax? baseList, SyntaxNode node, Microsoft.CodeAnalysis.Text.TextLineCollection lines)
    {
        var baseTypes = baseList?.Types
            .Select(t => t.Type.ToString())
            .ToList() ?? [];

        var line = lines.GetLineFromPosition(node.Span.Start).LineNumber + 1;
        return new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, kind, baseTypes, line);
    }

    private static TypeHierarchyEntry MakeCsEnumEntry(
        string filePath, EnumDeclarationSyntax e, Microsoft.CodeAnalysis.Text.TextLineCollection lines)
    {
        // Enums can specify an underlying type: enum Foo : byte
        var baseTypes = e.BaseList?.Types.Select(t => t.Type.ToString()).ToList()
                        ?? (IReadOnlyList<string>)[];
        var line = lines.GetLineFromPosition(e.Span.Start).LineNumber + 1;
        return new TypeHierarchyEntry(filePath.Replace('\\', '/'), e.Identifier.Text, "enum", baseTypes, line);
    }

    // ── TypeScript ────────────────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> ExtractTypeScript(string source, string filePath)
    {
        using var parser = new Parser();
        var tree    = parser.ParseString(source);
        var root    = tree.root_node();
        var results = new List<TypeHierarchyEntry>();

        CollectTsTypes(root, source, filePath, results);
        return results;
    }

    private static void CollectTsTypes(TSNode node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var nodeType = node.type();

        if (nodeType == "class_declaration")
        {
            var name      = GetFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectClassHeritage(node, source);
            var line      = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "class", baseTypes, line));
            // Recurse for nested classes
        }
        else if (nodeType == "interface_declaration")
        {
            var name      = GetFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectInterfaceExtends(node, source);
            var line      = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "interface", baseTypes, line));
        }
        else if (nodeType == "enum_declaration")
        {
            var name = GetFieldText(node, "name", source) ?? "<anonymous>";
            var line = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "enum", [], line));
        }

        for (uint i = 0; i < node.child_count(); i++)
            CollectTsTypes(node.child(i), source, filePath, results);
    }

    private static List<string> CollectClassHeritage(TSNode classNode, string source)
    {
        var baseTypes = new List<string>();

        for (uint i = 0; i < classNode.child_count(); i++)
        {
            var child = classNode.child(i);
            var ct    = child.type();

            if (ct == "class_heritage")
            {
                for (uint j = 0; j < child.child_count(); j++)
                {
                    var heritage = child.child(j);
                    var ht       = heritage.type();

                    if (ht == "extends_clause")
                    {
                        // "extends Foo" or "extends Foo<T>" — grab the type reference text
                        var typeRef = ExtractHeritageTypeText(heritage, source);
                        if (typeRef is not null)
                            baseTypes.Add(typeRef);
                    }
                    else if (ht == "implements_clause")
                    {
                        // "implements IFoo, IBar" — multiple type references
                        for (uint k = 0; k < heritage.child_count(); k++)
                        {
                            var impl = heritage.child(k);
                            if (impl.type() is "type_identifier" or "generic_type" or "member_type")
                            {
                                var start = (int)impl.start_offset();
                                var end   = (int)impl.end_offset();
                                baseTypes.Add(source[start..end].Trim());
                            }
                        }
                    }
                }
            }
        }

        return baseTypes;
    }

    private static List<string> CollectInterfaceExtends(TSNode ifaceNode, string source)
    {
        var baseTypes = new List<string>();

        for (uint i = 0; i < ifaceNode.child_count(); i++)
        {
            var child = ifaceNode.child(i);
            if (child.type() == "extends_type_clause")
            {
                for (uint j = 0; j < child.child_count(); j++)
                {
                    var t = child.child(j);
                    if (t.type() is "type_identifier" or "generic_type" or "member_type")
                    {
                        var start = (int)t.start_offset();
                        var end   = (int)t.end_offset();
                        baseTypes.Add(source[start..end].Trim());
                    }
                }
            }
        }

        return baseTypes;
    }

    private static string? ExtractHeritageTypeText(TSNode extendsClause, string source)
    {
        // extends_clause: "extends" <type_reference>
        // The type reference is usually the second child (after "extends" keyword)
        for (uint i = 0; i < extendsClause.child_count(); i++)
        {
            var child = extendsClause.child(i);
            var ct    = child.type();
            if (ct is "type_identifier" or "generic_type" or "member_type" or "identifier")
            {
                var start = (int)child.start_offset();
                var end   = (int)child.end_offset();
                return source[start..end].Trim();
            }
        }
        return null;
    }

    private static string? GetFieldText(TSNode node, string fieldName, string source)
    {
        var child = node.child_by_field_name(fieldName);
        return child.is_null() ? null : source[(int)child.start_offset()..(int)child.end_offset()];
    }
}
