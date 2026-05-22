using TreeSitterLanguagePack;

namespace ContextKing.Core.Ast.TreeSitter;

/// <summary>
/// Kotlin extractor using tree-sitter-kotlin via TreeSitterLanguagePack.
/// </summary>
public sealed class KotlinExtractor : TreeSitterExtractor
{
    protected override string LanguageName => "kotlin";

    protected override string[] ContainerNodeTypes => ["class_declaration", "object_declaration"];

    protected override string[] MemberNodeTypes =>
    [
        "function_declaration", "property_declaration",
        "class_declaration", "object_declaration", "primary_constructor", "secondary_constructor"
    ];

    protected override string[] TypeDeclarationNodeTypes =>
    [
        "class_declaration", "interface_declaration",
        "object_declaration"
    ];

    protected override string FunctionDeclarationNodeType => "function_declaration";

    protected override bool HasPrivateOrProtectedModifier(Node node, string source)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child?.Kind() == "modifiers")
            {
                var modCount = child.ChildCount();
                for (uint j = 0; j < modCount; j++)
                {
                    var mod = child.Child(j);
                    if (mod is not null)
                    {
                        var modText = SourceSlice(source, mod);
                        if (modText is "private" or "protected")
                            return true;
                    }
                }
            }
        }
        return false;
    }

    protected override string? GetMemberName(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "function_declaration")
        {
            return GetNodeName(node, source);
        }

        if (kind is "property_declaration")
        {
            return GetNodeName(node, source);
        }

        if (kind is "class_declaration" or "object_declaration")
        {
            return GetNodeName(node, source);
        }

        if (kind is "primary_constructor" or "secondary_constructor")
        {
            return "constructor";
        }

        return null;
    }

    protected override string BuildMemberSignature(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "primary_constructor" or "secondary_constructor")
            return BuildConstructorSig(node, source);

        if (kind is "class_declaration" or "object_declaration")
        {
            var name = GetNodeName(node, source) ?? "<unknown>";
            return IsEnumClass(node, source)
                ? $"enum class {name}"
                : $"{kind.Replace('_', ' ')} {name}";
        }

        if (kind is "property_declaration")
            return BuildPropertySig(node, source);

        return BuildFunSig(node, source);
    }

    private string BuildFunSig(Node node, string source)
    {
        var parts = new List<string>();

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            var ct = child.Kind();
            if (ct is "modifiers")
            {
                var modCount = child.ChildCount();
                for (uint j = 0; j < modCount; j++)
                {
                    var mod = child.Child(j);
                    if (mod is not null)
                    {
                        var mt = mod.Kind();
                        if (mt is "suspend_modifier" or "inline_modifier" or "override_modifier")
                            parts.Add(mod.Kind().Replace("_modifier", ""));
                        else if (mt is "visibility_modifier")
                            parts.Add(SourceSlice(source, mod));
                    }
                }
            }
            else if (ct == "simple_identifier")
            {
                break;
            }
        }

        var name = GetNodeName(node, source) ?? "<unknown>";
        parts.Add(name);

        var parms = GetFieldText(node, "value_parameters", source)
            ?? GetFieldText(node, "function_value_parameters", source)
            ?? ChildTextByKinds(node, source, "function_value_parameters");
        if (parms is not null)
            parts[^1] += parms;

        var retType = GetFieldText(node, "type", source) ?? ChildTextByKinds(node, source, "user_type");
        if (retType is not null)
            parts[^1] += ": " + retType;

        return string.Join(' ', parts);
    }

    private string BuildPropertySig(Node node, string source)
    {
        var parts = new List<string>();

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            var ct = child.Kind();
            if (ct is "modifiers")
            {
                var modCount = child.ChildCount();
                for (uint j = 0; j < modCount; j++)
                {
                    var mod = child.Child(j);
                    if (mod is not null)
                    {
                        var mt = mod.Kind();
                        if (mt is "visibility_modifier")
                            parts.Add(SourceSlice(source, mod));
                    }
                }
            }
        }

        var name = GetNodeName(node, source) ?? "<unknown>";
        parts.Add(name);

        var type = GetFieldText(node, "type", source);
        if (type is not null)
            parts[^1] += ": " + type;

        return string.Join(' ', parts);
    }

    protected override bool TryMatchConstructor(
        Node node,
        string source,
        string containingType,
        out string constructorName)
    {
        var kind = node.Kind();
        if (kind is not ("primary_constructor" or "secondary_constructor"))
        {
            constructorName = string.Empty;
            return false;
        }

        var leafType = containingType.Split('.').LastOrDefault();
        constructorName = string.IsNullOrWhiteSpace(leafType) || leafType == "<global>"
            ? "constructor"
            : leafType;
        return true;
    }

    private string BuildConstructorSig(Node node, string source)
    {
        var parts = new List<string>();
        var count = node.ChildCount();

        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child?.Kind() != "modifiers") continue;

            var modCount = child.ChildCount();
            for (uint j = 0; j < modCount; j++)
            {
                var mod = child.Child(j);
                if (mod is not null && mod.Kind() == "visibility_modifier")
                    parts.Add(SourceSlice(source, mod));
            }
        }

        var parameters = node.Kind() == "primary_constructor"
            ? SourceSlice(source, node)
            : GetFieldText(node, "class_parameters", source)
            ?? GetFieldText(node, "value_parameters", source)
            ?? GetFieldText(node, "function_value_parameters", source)
            ?? GetFieldText(node, "parameters", source)
            ?? ChildTextByKinds(node, source, "class_parameter", "function_value_parameters")
            ?? "()";

        parts.Add($"constructor{parameters}");
        return string.Join(' ', parts);
    }

    private static bool IsEnumClass(Node node, string source)
    {
        if (node.Kind() != "class_declaration")
            return false;

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            if (child.Kind() == "enum")
                return true;
            if (SourceSlice(source, child) == "enum")
                return true;
        }

        return false;
    }

    private static string? ChildTextByKinds(Node node, string source, params string[] kinds)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            if (Array.IndexOf(kinds, child.Kind()) >= 0)
                return SourceSlice(source, child);
        }

        return null;
    }
}
