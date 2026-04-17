using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Core.Ast.TypeScript;

/// <summary>
/// Extracts function, method, constructor, and property signatures from TypeScript/TSX files
/// using tree-sitter. Always reads live from disk; never uses cached data.
/// </summary>
public static class TsSignatureExtractor
{
    /// <summary>
    /// Extracts all member signatures from each file in <paramref name="filePaths"/>
    /// and writes them to <paramref name="writer"/> in the same tab-separated format as
    /// <see cref="SignatureExtractor"/>:
    ///   filepath:line\tcontainingType\tmemberName\tsignature
    /// </summary>
    public static void Extract(
        IEnumerable<string> filePaths,
        TextWriter writer,
        TextWriter? errorWriter = null)
    {
        errorWriter ??= Console.Error;

        foreach (var path in filePaths)
        {
            try
            {
                ExtractFromFile(path, writer);
            }
            catch (Exception ex)
            {
                errorWriter.WriteLine($"[ck-signatures] WARN: skipping '{path}': {ex.Message}");
            }
        }
    }

    private static void ExtractFromFile(string path, TextWriter writer)
    {
        var source = File.ReadAllText(path);
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();

        Visit(root, source, path, containingType: "<global>", writer);
    }

    private static void Visit(
        TSNode node, string source, string path,
        string containingType, TextWriter writer)
    {
        var type = node.type();

        switch (type)
        {
            case "class_declaration":
            {
                var name = GetFieldText(node, "name", source) ?? "<anonymous>";
                var newContainer = containingType == "<global>" ? name : $"{containingType}.{name}";
                VisitChildren(node, source, path, newContainer, writer);
                return;
            }

            case "interface_declaration":
            {
                var name = GetFieldText(node, "name", source) ?? "<anonymous>";
                var newContainer = containingType == "<global>" ? name : $"{containingType}.{name}";
                VisitChildren(node, source, path, newContainer, writer);
                return;
            }

            case "method_definition":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = BuildMethodSignature(node, source);
                Emit(writer, path, node, containingType, name, sig);
                return; // don't recurse into body
            }

            case "method_signature":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = BuildMethodSignature(node, source);
                Emit(writer, path, node, containingType, name, sig);
                return;
            }

            case "public_field_definition":
            case "property_signature":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = BuildFieldSignature(node, source);
                Emit(writer, path, node, containingType, name, sig);
                return;
            }

            case "function_declaration":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = BuildFunctionSignature(node, source);
                Emit(writer, path, node, containingType, name, sig);
                return;
            }

            case "type_alias_declaration":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = $"type {name}";
                Emit(writer, path, node, containingType, name, sig);
                return;
            }

            case "enum_declaration":
            {
                var name = GetFieldText(node, "name", source) ?? "<unknown>";
                var sig = $"enum {name}";
                Emit(writer, path, node, containingType, name, sig);
                return;
            }
        }

        VisitChildren(node, source, path, containingType, writer);
    }

    private static void VisitChildren(
        TSNode node, string source, string path,
        string containingType, TextWriter writer)
    {
        for (uint i = 0; i < node.child_count(); i++)
            Visit(node.child(i), source, path, containingType, writer);
    }

    // ── Signature builders ───────────────────────────────────────────────────

    private static string BuildMethodSignature(TSNode node, string source)
    {
        var parts = new List<string>();

        // Collect modifiers (accessibility_modifier, async, static, get, set, etc.)
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            var ct = child.type();
            if (ct is "accessibility_modifier" or "override_modifier" or "readonly")
                parts.Add(child.text(source));
            else if (ct is "static")
                parts.Add("static");
            else if (ct is "async")
                parts.Add("async");
            else if (ct is "get")
                parts.Add("get");
            else if (ct is "set")
                parts.Add("set");
            else if (ct is "property_identifier" or "identifier" or "computed_property_name")
                break; // name comes next — stop collecting modifiers
        }

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        parts.Add(name);

        var parms = GetFieldText(node, "parameters", source);
        if (parms is not null)
            parts[^1] += parms; // append directly: "processPayment(id: string)"

        var retType = GetFieldText(node, "return_type", source);
        if (retType is not null)
            parts[^1] += retType; // append ": Promise<Result>"

        return string.Join(' ', parts);
    }

    private static string BuildFunctionSignature(TSNode node, string source)
    {
        var parts = new List<string> { "function" };

        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            if (child.type() is "async")
                parts.Insert(0, "async");
        }

        var name = GetFieldText(node, "name", source) ?? "<unknown>";
        var parms = GetFieldText(node, "parameters", source) ?? "()";
        var retType = GetFieldText(node, "return_type", source) ?? "";

        parts.Add($"{name}{parms}{retType}");
        return string.Join(' ', parts);
    }

    private static string BuildFieldSignature(TSNode node, string source)
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

        // Type annotation
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            if (child.type() == "type_annotation")
            {
                parts[^1] += child.text(source);
                break;
            }
        }

        // Optional marker (?)
        for (uint i = 0; i < node.child_count(); i++)
        {
            var child = node.child(i);
            if (child.type() == "?" && i > 0)
            {
                // Insert ? before type annotation in the name part
                var nameIdx = parts.Count - 1;
                var colonIdx = parts[nameIdx].IndexOf(':');
                if (colonIdx >= 0)
                    parts[nameIdx] = parts[nameIdx].Insert(colonIdx, "?");
                break;
            }
        }

        return string.Join(' ', parts);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void Emit(
        TextWriter writer, string path, TSNode node,
        string containingType, string memberName, string signature)
    {
        var line = (int)node.start_point().row + 1; // 1-based
        var normalizedPath = path.Replace('\\', '/');
        writer.WriteLine($"{normalizedPath}:{line}\t{containingType}\t{memberName}\t{signature}");
    }

    private static string? GetFieldText(TSNode node, string fieldName, string source)
    {
        var child = node.child_by_field_name(fieldName);
        return child.is_null() ? null : child.text(source);
    }
}
