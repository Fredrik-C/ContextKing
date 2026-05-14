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
        "class_declaration", "object_declaration", "enum_class"
    ];

    protected override string[] TypeDeclarationNodeTypes =>
    [
        "class_declaration", "interface_declaration",
        "object_declaration", "enum_class"
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
            return GetFieldText(node, "name", source);
        }

        if (kind is "property_declaration")
        {
            return GetFieldText(node, "name", source);
        }

        if (kind is "class_declaration" or "object_declaration")
        {
            var name = GetFieldText(node, "name", source);
            return name;
        }

        if (kind is "enum_class")
        {
            var name = GetFieldText(node, "name", source);
            return name is not null ? $"enum class {name}" : null;
        }

        return null;
    }

    protected override string BuildMemberSignature(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "class_declaration" or "object_declaration" or "enum_class")
        {
            var name = GetFieldText(node, "name", source) ?? "<unknown>";
            return $"{kind.Replace('_', ' ')} {name}";
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

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        parts.Add(name);

        var parms = GetFieldText(node, "value_parameters", source);
        if (parms is not null)
            parts[^1] += parms;

        var retType = GetFieldText(node, "type", source);
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

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        parts.Add(name);

        var type = GetFieldText(node, "type", source);
        if (type is not null)
            parts[^1] += ": " + type;

        return string.Join(' ', parts);
    }
}
