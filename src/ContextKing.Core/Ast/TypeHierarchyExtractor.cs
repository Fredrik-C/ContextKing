using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;
using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts type hierarchy information (class/interface declarations with base types)
/// from C#, TypeScript/TSX, Kotlin, and Python files. Always reads live from disk; never uses cached data.
/// </summary>
public static class TypeHierarchyExtractor
{
    public static IReadOnlyList<TypeHierarchyEntry> Extract(string filePath)
    {
        var source = File.ReadAllText(filePath);

        if (SupportedLanguages.IsTypeScript(filePath))
            return ExtractTypeScript(source, filePath);

        if (SupportedLanguages.IsKotlin(filePath))
            return ExtractTreeSitter(source, filePath, "kotlin");

        if (SupportedLanguages.IsPython(filePath))
            return ExtractTreeSitter(source, filePath, "python");

        return ExtractCSharp(source, filePath);
    }

    // ── Tree-sitter dispatch ──────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> ExtractTreeSitter(string source, string filePath, string languageName)
    {
        using var parser = TreeSitterLanguagePack.Parser.Default();
        parser.SetLanguage(languageName);
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var results = new List<TypeHierarchyEntry>();

        return languageName switch
        {
            "kotlin" => CollectKtTypes(root, source, filePath, results),
            "python" => CollectPyTypes(root, source, filePath, results),
            _ => []
        };
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
        using var parser = new TypeScriptParser.Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var results = new List<TypeHierarchyEntry>();

        CollectTsTypes(root, source, filePath, results);
        return results;
    }

    private static IReadOnlyList<TypeHierarchyEntry> CollectTsTypes(TSNode node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var nodeType = node.type();

        if (nodeType == "class_declaration")
        {
            var name = GetTsFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectClassHeritage(node, source);
            var line = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "class", baseTypes, line));
        }
        else if (nodeType == "interface_declaration")
        {
            var name = GetTsFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectInterfaceExtends(node, source);
            var line = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "interface", baseTypes, line));
        }
        else if (nodeType == "enum_declaration")
        {
            var name = GetTsFieldText(node, "name", source) ?? "<anonymous>";
            var line = (int)node.start_point().row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "enum", [], line));
        }

        for (uint i = 0; i < node.child_count(); i++)
            CollectTsTypes(node.child(i), source, filePath, results);
        return results;
    }

    private static List<string> CollectClassHeritage(TSNode classNode, string source)
    {
        var baseTypes = new List<string>();

        for (uint i = 0; i < classNode.child_count(); i++)
        {
            var child = classNode.child(i);
            var ct = child.type();

            if (ct == "class_heritage")
            {
                for (uint j = 0; j < child.child_count(); j++)
                {
                    var heritage = child.child(j);
                    var ht = heritage.type();

                    if (ht == "extends_clause")
                    {
                        var typeRef = ExtractHeritageTypeText(heritage, source);
                        if (typeRef is not null)
                            baseTypes.Add(typeRef);
                    }
                    else if (ht == "implements_clause")
                    {
                        for (uint k = 0; k < heritage.child_count(); k++)
                        {
                            var impl = heritage.child(k);
                            if (impl.type() is "type_identifier" or "generic_type" or "member_type")
                            {
                                var text = impl.text(source).Trim();
                                if (text.Length > 0)
                                    baseTypes.Add(text);
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
                        var text = t.text(source).Trim();
                        if (text.Length > 0)
                            baseTypes.Add(text);
                    }
                }
            }
        }

        return baseTypes;
    }

    private static string? ExtractHeritageTypeText(TSNode extendsClause, string source)
    {
        var count = extendsClause.child_count();
        for (uint i = 0; i < count; i++)
        {
            var child = extendsClause.child(i);
            var ct = child.type();
            if (ct is "type_identifier" or "generic_type" or "member_type" or "identifier")
            {
                return child.text(source).Trim();
            }
        }
        return null;
    }

    private static string? GetTsFieldText(TSNode node, string fieldName, string source)
    {
        var child = node.child_by_field_name(fieldName);
        return child.is_null() ? null : child.text(source);
    }

    // ── Kotlin ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> CollectKtTypes(Node node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var kind = node.Kind();

        if (kind == "class_declaration")
        {
            var name = GetKotlinTypeName(node, source) ?? "<anonymous>";
            var baseTypes = CollectKotlinHeritage(node, source);
            var line = (int)node.StartPosition().Row + 1;
            var typeKind = IsKotlinTokenPresent(node, source, "enum")
                ? "enum"
                : IsKotlinTokenPresent(node, source, "interface") ? "interface" : "class";
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, typeKind, baseTypes, line));
        }
        else if (kind == "object_declaration")
        {
            var name = GetKotlinTypeName(node, source) ?? "<anonymous>";
            var baseTypes = CollectKotlinHeritage(node, source);
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "object", baseTypes, line));
        }

        WalkChildren(node, child => CollectKtTypes(child, source, filePath, results));
        return results;
    }

    private static List<string> CollectKotlinHeritage(Node classNode, string source)
    {
        var baseTypes = new List<string>();

        WalkChildren(classNode, child =>
        {
            var ct = child.Kind();
            if (ct is "superclass" or "super_interfaces")
            {
                WalkChildren(child, t =>
                {
                    if (t.Kind() is "type_identifier" or "type_projection")
                    {
                        // For type_projection, extract the inner type_identifier
                        var inner = t.ChildByFieldName("type");
                        if (inner is not null)
                        {
                            var text = NodeSourceSlice(source, inner).Trim();
                            if (text.Length > 0)
                                baseTypes.Add(text);
                        }
                        else
                        {
                            var text = NodeSourceSlice(source, t).Trim();
                            if (text.Length > 0)
                                baseTypes.Add(text);
                        }
                    }
                    else if (t.Kind() is "user_type" or "nullable_type")
                    {
                        var text = NodeSourceSlice(source, t).Trim();
                        // Strip trailing ? for nullable types
                        if (t.Kind() == "nullable_type" && text.EndsWith('?'))
                            text = text[..^1].Trim();
                        if (text.Length > 0)
                            baseTypes.Add(text);
                    }
                });
            }
        });

        return baseTypes;
    }

    // ── Python ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> CollectPyTypes(Node node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var kind = node.Kind();

        if (kind == "class_definition")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var bases = GetNodeFieldText(node, "superclasses", source);
            var baseTypes = bases is not null
                ? new List<string> { bases.Trim('(', ')').Trim() }
                : new List<string>();
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "class", baseTypes, line));
        }

        WalkChildren(node, child => CollectPyTypes(child, source, filePath, results));
        return results;
    }

    // ── Tree-sitter helpers ───────────────────────────────────────────────────

    private static void WalkChildren(Node node, Action<Node> action)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is not null)
                action(child);
        }
    }

    private static string? GetNodeFieldText(Node node, string fieldName, string source)
    {
        var child = node.ChildByFieldName(fieldName);
        return child is null ? null : NodeSourceSlice(source, child);
    }

    private static string NodeSourceSlice(string source, Node node)
    {
        var start = (int)node.StartByte();
        var end = (int)node.EndByte();
        return source[start..end];
    }

    private static string? GetKotlinTypeName(Node node, string source)
    {
        var byField = GetNodeFieldText(node, "name", source);
        if (!string.IsNullOrWhiteSpace(byField))
            return byField;

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            if (child.Kind() is "type_identifier" or "simple_identifier" or "identifier")
                return NodeSourceSlice(source, child);
        }

        return null;
    }

    private static bool IsKotlinTokenPresent(Node node, string source, string token)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            if (child.Kind() == token)
                return true;
            if (NodeSourceSlice(source, child) == token)
                return true;
        }

        return false;
    }
}
