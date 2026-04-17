using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast.TypeScript;

/// <summary>
/// Extracts source content for a named member from a TypeScript/TSX file using tree-sitter.
/// Returns exact line and char-offset spans within the original file alongside the requested
/// content slice. Always reads live from disk; never uses cached data.
/// </summary>
public static class TsMethodSourceExtractor
{
    public static IReadOnlyList<MethodSourceResult> Extract(
        string filePath,
        string memberName,
        string? typeFilter,
        SourceMode mode)
    {
        var source = File.ReadAllText(filePath);
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var results = new List<MethodSourceResult>();

        FindMembers(root, source, filePath, memberName, typeFilter, mode, "<global>", results);

        return results;
    }

    /// <summary>
    /// Returns all member names in the file. Used for fuzzy-match suggestions
    /// when a member name is not found.
    /// </summary>
    public static IReadOnlyList<string> GetAllMemberNames(string filePath)
    {
        var source = File.ReadAllText(filePath);
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var names = new List<string>();
        CollectMemberNames(root, source, names);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void CollectMemberNames(TSNode node, string source, List<string> names)
    {
        var nodeType = node.type();
        if (IsMemberNode(nodeType))
        {
            var name = GetFieldText(node, "name", source);
            if (name is not null)
                names.Add(name);
            return;
        }

        for (uint i = 0; i < node.child_count(); i++)
            CollectMemberNames(node.child(i), source, names);
    }

    private static void FindMembers(
        TSNode node, string source, string filePath,
        string memberName, string? typeFilter, SourceMode mode,
        string containingType, List<MethodSourceResult> results)
    {
        var nodeType = node.type();

        // Track containing type for class/interface scoping
        if (nodeType is "class_declaration" or "interface_declaration")
        {
            var typeName = GetFieldText(node, "name", source) ?? "<anonymous>";
            var newContainer = containingType == "<global>" ? typeName : $"{containingType}.{typeName}";

            // Visit children with updated container
            for (uint i = 0; i < node.child_count(); i++)
                FindMembers(node.child(i), source, filePath, memberName, typeFilter, mode, newContainer, results);
            return;
        }

        // Check if this node is a matching member
        if (IsMemberNode(nodeType))
        {
            var name = GetFieldText(node, "name", source);
            if (name is not null && name.Equals(memberName, StringComparison.Ordinal))
            {
                if (typeFilter is null ||
                    containingType.Equals(typeFilter, StringComparison.Ordinal) ||
                    containingType.EndsWith("." + typeFilter, StringComparison.Ordinal))
                {
                    var result = BuildResult(node, source, filePath, name, containingType, mode);
                    if (result is not null)
                        results.Add(result);
                }
            }
            return; // don't recurse into member bodies
        }

        // Recurse
        for (uint i = 0; i < node.child_count(); i++)
            FindMembers(node.child(i), source, filePath, memberName, typeFilter, mode, containingType, results);
    }

    private static bool IsMemberNode(string nodeType) => nodeType is
        "method_definition" or "method_signature" or
        "function_declaration" or
        "public_field_definition" or "property_signature" or
        "type_alias_declaration" or "enum_declaration";

    private static MethodSourceResult? BuildResult(
        TSNode node, string source, string filePath,
        string memberName, string containingType, SourceMode mode)
    {
        var (content, startChar, endChar) = ExtractContent(node, source, mode);
        if (content is null) return null;

        var startLine = CountLine(source, startChar);
        var endLine = CountLine(source, Math.Max(startChar, endChar - 1));

        return new MethodSourceResult(
            File: filePath,
            MemberName: memberName,
            ContainingType: containingType,
            Signature: BuildSignature(node, source),
            Mode: ModeLabel(mode),
            StartLine: startLine,
            EndLine: endLine,
            StartChar: startChar,
            EndChar: endChar,
            Content: content);
    }

    // ── Content extraction ───────────────────────────────────────────────────

    private static (string? content, int startChar, int endChar) ExtractContent(
        TSNode node, string source, SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly     => SignatureContent(node, source),
        SourceMode.SignaturePlusBody => FullContent(node, source),
        SourceMode.BodyOnly          => BodyContent(node, source),
        SourceMode.BodyWithoutComments => BodyNoComments(node, source),
        _ => FullContent(node, source)
    };

    private static (string content, int startChar, int endChar) FullContent(
        TSNode node, string source)
    {
        var start = (int)node.start_offset();
        var end = (int)node.end_offset();
        return (source[start..end], start, end);
    }

    private static (string content, int startChar, int endChar) SignatureContent(
        TSNode node, string source)
    {
        var body = node.child_by_field_name("body");
        int start = (int)node.start_offset();

        if (!body.is_null())
        {
            // Signature = everything before the body, trimmed
            var sigEnd = (int)body.start_offset();
            while (sigEnd > start && char.IsWhiteSpace(source[sigEnd - 1]))
                sigEnd--;
            return (source[start..sigEnd], start, sigEnd);
        }

        // No body (interface member, type alias, etc.) — full text
        var end = (int)node.end_offset();
        return (source[start..end], start, end);
    }

    private static (string? content, int startChar, int endChar) BodyContent(
        TSNode node, string source)
    {
        var body = node.child_by_field_name("body");
        if (body.is_null())
        {
            var s = (int)node.start_offset();
            return (null, s, s);
        }

        var start = (int)body.start_offset();
        var end = (int)body.end_offset();
        return (source[start..end], start, end);
    }

    private static (string? content, int startChar, int endChar) BodyNoComments(
        TSNode node, string source)
    {
        var body = node.child_by_field_name("body");
        if (body.is_null())
        {
            var s = (int)node.start_offset();
            return (null, s, s);
        }

        var start = (int)body.start_offset();
        var end = (int)body.end_offset();
        var bodyText = source[start..end];

        // Strip // and /* */ comments using a simple scan
        var stripped = StripComments(bodyText);
        return (stripped, start, end);
    }

    // ── Signature builder ────────────────────────────────────────────────────

    private static string BuildSignature(TSNode node, string source)
    {
        var nodeType = node.type();

        if (nodeType is "type_alias_declaration")
        {
            var name = GetFieldText(node, "name", source) ?? "<unknown>";
            return $"type {name}";
        }

        if (nodeType is "enum_declaration")
        {
            var name = GetFieldText(node, "name", source) ?? "<unknown>";
            return $"enum {name}";
        }

        if (nodeType is "public_field_definition" or "property_signature")
        {
            return BuildFieldSig(node, source);
        }

        // method_definition, method_signature, function_declaration
        return BuildMethodSig(node, source);
    }

    private static string BuildMethodSig(TSNode node, string source)
    {
        var parts = new List<string>();

        // Collect modifiers
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            var ct = child.type();
            if (ct is "accessibility_modifier" or "override_modifier" or "readonly")
                parts.Add(child.text(source));
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

    private static string BuildFieldSig(TSNode node, string source)
    {
        var parts = new List<string>();
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            var ct = child.type();
            if (ct is "accessibility_modifier" or "override_modifier" or "readonly")
                parts.Add(child.text(source));
            else if (ct is "static")
                parts.Add("static");
        }

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        parts.Add(name);

        for (uint i = 0; i < node.child_count(); i++)
        {
            if (node.child(i).type() == "type_annotation")
            {
                parts[^1] += node.child(i).text(source);
                break;
            }
        }

        return string.Join(' ', parts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? GetFieldText(TSNode node, string fieldName, string source)
    {
        var child = node.child_by_field_name(fieldName);
        return child.is_null() ? null : child.text(source);
    }

    private static int CountLine(string source, int charOffset)
    {
        var line = 1;
        for (var i = 0; i < charOffset && i < source.Length; i++)
        {
            if (source[i] == '\n') line++;
        }
        return line;
    }

    private static string ModeLabel(SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly        => "signature_only",
        SourceMode.SignaturePlusBody    => "signature_plus_body",
        SourceMode.BodyOnly            => "body_only",
        SourceMode.BodyWithoutComments => "body_without_comments",
        _                              => "signature_plus_body"
    };

    /// <summary>
    /// Simple comment stripper for TypeScript/JavaScript.
    /// Removes // line comments and /* block comments */ while preserving
    /// strings and template literals (basic handling).
    /// </summary>
    internal static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            // String literals
            if (text[i] is '\'' or '"' or '`')
            {
                var quote = text[i];
                sb.Append(text[i++]);
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        sb.Append(text[i++]);
                    }
                    sb.Append(text[i++]);
                }
                if (i < text.Length) sb.Append(text[i++]); // closing quote
                continue;
            }

            // Line comment
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            // Block comment
            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                if (i + 1 < text.Length) i += 2; // skip */
                continue;
            }

            sb.Append(text[i++]);
        }

        return sb.ToString();
    }
}
