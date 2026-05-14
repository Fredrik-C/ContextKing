using TreeSitterLanguagePack;

namespace ContextKing.Core.Ast.TreeSitter;

/// <summary>
/// Python extractor using tree-sitter-python via TreeSitterLanguagePack.
/// </summary>
public sealed class PythonExtractor : TreeSitterExtractor
{
    protected override string LanguageName => "python";

    protected override string[] ContainerNodeTypes => ["class_definition"];

    protected override string[] MemberNodeTypes =>
    [
        "function_definition", "decorated_definition"
    ];

    protected override string[] TypeDeclarationNodeTypes => ["class_definition"];

    protected override string FunctionDeclarationNodeType => "function_definition";

    protected override bool HasPrivateOrProtectedModifier(Node node, string source)
    {
        // Python doesn't have access modifiers; all names are public-accessible.
        // Names with leading underscore are conventionally private (not enforced).
        return false;
    }

    protected override string? GetMemberName(Node node, string source)
    {
        var kind = node.Kind();

        if (kind is "function_definition")
        {
            var name = GetFieldText(node, "name", source);
            return name;
        }

        if (kind is "decorated_definition")
        {
            // For decorated functions, find the actual function_definition child
            var count = node.ChildCount();
            for (uint i = 0; i < count; i++)
            {
                var child = node.Child(i);
                if (child?.Kind() == "function_definition")
                {
                    var name = GetFieldText(child, "name", source);
                    return name;
                }
            }
            // Fallback: try field name on the wrapper
            return GetFieldText(node, "name", source);
        }

        return null;
    }

    protected override string BuildMemberSignature(Node node, string source)
    {
        var kind = node.Kind();
        Node funNode = node;

        if (kind is "decorated_definition")
        {
            // Find the inner function_definition
            var count = node.ChildCount();
            for (uint i = 0; i < count; i++)
            {
                var child = node.Child(i);
                if (child?.Kind() == "function_definition")
                {
                    funNode = child;
                    break;
                }
            }
        }

        var parts = new List<string>();

        // async
        var fc = funNode.ChildCount();
        for (uint i = 0; i < fc; i++)
        {
            var child = funNode.Child(i);
            if (child?.Kind() == "async")
            {
                parts.Add("async");
                break;
            }
        }

        parts.Add("def");

        var name = GetFieldText(funNode, "name", source) ?? "<unknown>";
        parts.Add(name);

        var parms = GetFieldText(funNode, "parameters", source);
        if (parms is not null)
            parts[^1] += parms;

        var retType = GetFieldText(funNode, "return_type", source);
        if (retType is not null)
            parts[^1] += " -> " + retType.TrimStart();

        return string.Join(' ', parts);
    }
}
