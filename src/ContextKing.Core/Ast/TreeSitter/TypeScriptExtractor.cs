using TreeSitterLanguagePack;

namespace ContextKing.Core.Ast.TreeSitter;

/// <summary>
/// TypeScript/TSX extractor using tree-sitter via TreeSitterLanguagePack.
/// Replaces the old <see cref="TypeScript.TsSignatureExtractor"/>,
/// <see cref="TypeScript.TsMethodSourceExtractor"/>, and
/// <see cref="TypeScript.TsPublicMethodNameExtractor"/>.
/// </summary>
public sealed class TypeScriptExtractor : TreeSitterExtractor
{
    private readonly string _languageName;

    public TypeScriptExtractor(string languageName = "typescript")
    {
        _languageName = languageName;
    }

    protected override string LanguageName => _languageName;

    protected override string[] ContainerNodeTypes => ["class_declaration", "interface_declaration"];

    protected override string[] MemberNodeTypes =>
    [
        "method_definition", "method_signature",
        "public_field_definition", "property_signature",
        "type_alias_declaration", "enum_declaration"
    ];

    protected override string[] TypeDeclarationNodeTypes =>
    [
        "class_declaration", "interface_declaration",
        "type_alias_declaration", "enum_declaration"
    ];

    protected override string FunctionDeclarationNodeType => "function_declaration";

    protected override bool HasPrivateOrProtectedModifier(Node node, string source)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child?.Kind() == "accessibility_modifier")
            {
                var mod = SourceSlice(source, child);
                return mod is "private" or "protected";
            }
        }
        return false;
    }

    protected override string? GetMemberName(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "method_definition" or "method_signature" or "function_declaration"
            or "public_field_definition" or "property_signature")
        {
            return GetFieldText(node, "name", source);
        }

        if (kind is "type_alias_declaration")
        {
            var name = GetFieldText(node, "name", source);
            return name is not null ? $"type {name}" : null;
        }

        if (kind is "enum_declaration")
        {
            var name = GetFieldText(node, "name", source);
            return name is not null ? $"enum {name}" : null;
        }

        return null;
    }

    protected override string BuildMemberSignature(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "type_alias_declaration")
        {
            var name = GetFieldText(node, "name", source) ?? "<unknown>";
            return $"type {name}";
        }

        if (kind is "enum_declaration")
        {
            var name = GetFieldText(node, "name", source) ?? "<unknown>";
            return $"enum {name}";
        }

        if (kind is "public_field_definition" or "property_signature")
            return BuildFieldSig(node, source);

        return BuildMethodSig(node, source);
    }

    private string BuildMethodSig(Node node, string source)
    {
        var parts = new List<string>();

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            var ct = child.Kind();
            if (ct is "accessibility_modifier" or "override_modifier" or "readonly")
                parts.Add(SourceSlice(source, child));
            else if (ct is "static" or "async" or "get" or "set")
                parts.Add(ct);
            else if (ct is "function")
                parts.Add("function");
            else if (ct is "property_identifier" or "identifier" or "computed_property_name")
                break;
        }

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        var parms = GetFieldText(node, "parameters", source) ?? "";
        var retType = GetFieldText(node, "return_type", source) ?? "";
        parts.Add($"{name}{parms}{retType}");

        return string.Join(' ', parts);
    }

    private string BuildFieldSig(Node node, string source)
    {
        var parts = new List<string>();

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            var ct = child.Kind();
            if (ct is "accessibility_modifier" or "override_modifier" or "readonly")
                parts.Add(SourceSlice(source, child));
            else if (ct is "static")
                parts.Add("static");
        }

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        parts.Add(name);

        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child?.Kind() == "type_annotation")
            {
                parts[^1] += SourceSlice(source, child);
                break;
            }
        }

        return string.Join(' ', parts);
    }
}
