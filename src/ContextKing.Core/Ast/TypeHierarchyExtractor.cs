using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;

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
        {
            var lang = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "tsx" : "typescript";
            return ExtractTreeSitter(source, filePath, lang);
        }

        if (SupportedLanguages.IsKotlin(filePath))
            return ExtractTreeSitter(source, filePath, "kotlin");

        if (SupportedLanguages.IsPython(filePath))
            return ExtractTreeSitter(source, filePath, "python");

        return ExtractCSharp(source, filePath);
    }

    // ── Tree-sitter dispatch ──────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> ExtractTreeSitter(string source, string filePath, string languageName)
    {
        using var parser = Parser.Default();
        parser.SetLanguage(languageName);
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var results = new List<TypeHierarchyEntry>();

        return languageName switch
        {
            "typescript" => CollectTsTypes(root, source, filePath, results),
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

    private static IReadOnlyList<TypeHierarchyEntry> CollectTsTypes(Node node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var kind = node.Kind();

        if (kind == "class_declaration")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectClassHeritage(node, source);
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "class", baseTypes, line));
        }
        else if (kind == "interface_declaration")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectInterfaceExtends(node, source);
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "interface", baseTypes, line));
        }
        else if (kind == "enum_declaration")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "enum", [], line));
        }

        WalkChildren(node, child => CollectTsTypes(child, source, filePath, results));
        return results;
    }

    private static List<string> CollectClassHeritage(Node classNode, string source)
    {
        var baseTypes = new List<string>();

        WalkChildren(classNode, child =>
        {
            var ct = child.Kind();

            if (ct == "class_heritage")
            {
                WalkChildren(child, heritage =>
                {
                    var ht = heritage.Kind();

                    if (ht == "extends_clause")
                    {
                        var typeRef = ExtractHeritageTypeText(heritage, source);
                        if (typeRef is not null)
                            baseTypes.Add(typeRef);
                    }
                    else if (ht == "implements_clause")
                    {
                        WalkChildren(heritage, impl =>
                        {
                            if (impl.Kind() is "type_identifier" or "generic_type" or "member_type")
                            {
                                var text = NodeSourceSlice(source, impl).Trim();
                                if (text.Length > 0)
                                    baseTypes.Add(text);
                            }
                        });
                    }
                });
            }
        });

        return baseTypes;
    }

    private static List<string> CollectInterfaceExtends(Node ifaceNode, string source)
    {
        var baseTypes = new List<string>();

        WalkChildren(ifaceNode, child =>
        {
            if (child.Kind() == "extends_type_clause")
            {
                WalkChildren(child, t =>
                {
                    if (t.Kind() is "type_identifier" or "generic_type" or "member_type")
                    {
                        var text = NodeSourceSlice(source, t).Trim();
                        if (text.Length > 0)
                            baseTypes.Add(text);
                    }
                });
            }
        });

        return baseTypes;
    }

    private static string? ExtractHeritageTypeText(Node extendsClause, string source)
    {
        var count = extendsClause.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = extendsClause.Child(i);
            if (child is null) continue;
            var ct = child.Kind();
            if (ct is "type_identifier" or "generic_type" or "member_type" or "identifier")
            {
                return NodeSourceSlice(source, child).Trim();
            }
        }
        return null;
    }

    // ── Kotlin ────────────────────────────────────────────────────────────────

    private static IReadOnlyList<TypeHierarchyEntry> CollectKtTypes(Node node, string source, string filePath, List<TypeHierarchyEntry> results)
    {
        var kind = node.Kind();

        if (kind is "class_declaration" or "object_declaration")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectKotlinHeritage(node, source);
            var line = (int)node.StartPosition().Row + 1;
            var typeKind = kind == "object_declaration" ? "object" : "class";
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, typeKind, baseTypes, line));
        }
        else if (kind == "interface_declaration")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var baseTypes = CollectKotlinInterfaces(node, source);
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "interface", baseTypes, line));
        }
        else if (kind == "enum_class")
        {
            var name = GetNodeFieldText(node, "name", source) ?? "<anonymous>";
            var line = (int)node.StartPosition().Row + 1;
            results.Add(new TypeHierarchyEntry(filePath.Replace('\\', '/'), name, "enum class", [], line));
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
                        if (ct == "nullable_type" && text.EndsWith('?'))
                            text = text[..^1].Trim();
                        if (text.Length > 0)
                            baseTypes.Add(text);
                    }
                });
            }
        });

        return baseTypes;
    }

    private static List<string> CollectKotlinInterfaces(Node ifaceNode, string source)
    {
        return CollectKotlinHeritage(ifaceNode, source); // same pattern for super_interfaces
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
}
