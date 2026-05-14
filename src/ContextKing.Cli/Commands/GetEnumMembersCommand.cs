using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;

namespace ContextKing.Cli.Commands;

internal static class GetEnumMembersCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        var positional = reader.RemainingPositionals();
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("[ck get-enum-members] Error: file path and enum name are required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        var enumName = positional[1];

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[ck get-enum-members] Error: file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        try
        {
            EnumMembersResult? results = null;

            if (SupportedLanguages.IsTypeScript(filePath))
            {
                var lang = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "tsx" : "typescript";
                results = ExtractTreeSitterEnum(filePath, enumName, lang);
            }
            else if (SupportedLanguages.IsKotlin(filePath))
                results = ExtractTreeSitterEnum(filePath, enumName, "kotlin");
            else if (SupportedLanguages.IsPython(filePath))
                results = ExtractPythonEnum(filePath, enumName);
            else
                results = ExtractCSharp(filePath, enumName);

            if (results is null)
            {
                Console.Error.WriteLine($"[ck get-enum-members] No enum '{enumName}' found in '{filePath}'.");
                Console.WriteLine("{}");
                return Task.FromResult(1);
            }

            Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck get-enum-members] Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static EnumMembersResult? ExtractCSharp(string filePath, string enumName)
    {
        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root = tree.GetRoot();
        var lines = tree.GetText().Lines;

        foreach (var node in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            if (!node.Identifier.Text.Equals(enumName, StringComparison.Ordinal))
                continue;

            var members = node.Members.Select(m => m.Identifier.Text).ToArray();
            var span = node.Span;
            return new EnumMembersResult(
                File: filePath,
                EnumName: enumName,
                StartLine: lines.GetLineFromPosition(span.Start).LineNumber + 1,
                EndLine: lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber + 1,
                Members: members);
        }

        return null;
    }

    private static EnumMembersResult? ExtractTreeSitterEnum(string filePath, string enumName, string languageName)
    {
        var source = File.ReadAllText(filePath);
        using var parser = Parser.Default();
        parser.SetLanguage(languageName);
        var tree = parser.Parse(source);
        if (tree is null) return null;
        var root = tree.RootNode();
        if (root is null) return null;

        var enumNodeType = languageName switch
        {
            "typescript" => "enum_declaration",
            "kotlin" => "enum_class",
            _ => "enum_declaration"
        };

        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Kind() == enumNodeType)
            {
                var nameNode = node.ChildByFieldName("name");
                if (nameNode is not null)
                {
                    var name = source[(int)nameNode.StartByte()..(int)nameNode.EndByte()];
                    if (name.Equals(enumName, StringComparison.Ordinal))
                    {
                        var members = new List<string>();
                        var count = node.ChildCount();
                        for (uint i = 0; i < count; i++)
                        {
                            var child = node.Child(i);
                            if (child is null) continue;
                            var ck = child.Kind();
                            if (ck is "enum_body" or "enum_class_body")
                            {
                                var mc = child.ChildCount();
                                for (uint j = 0; j < mc; j++)
                                {
                                    var item = child.Child(j);
                                    if (item is null) continue;
                                    if (item.Kind() is "enum_assignment" or "enum_entry")
                                    {
                                        var itemName = item.ChildByFieldName("name");
                                        if (itemName is not null)
                                            members.Add(source[(int)itemName.StartByte()..(int)itemName.EndByte()]);
                                    }
                                }
                            }
                        }

                        return new EnumMembersResult(
                            File: filePath,
                            EnumName: enumName,
                            StartLine: CountLine(source, (int)node.StartByte()),
                            EndLine: CountLine(source, Math.Max((int)node.StartByte(), (int)node.EndByte() - 1)),
                            Members: members.ToArray());
                    }
                }
            }

            var childCount = node.ChildCount();
            for (uint i = 0; i < childCount; i++)
            {
                var child = node.Child(i);
                if (child is not null)
                    stack.Push(child);
            }
        }

        return null;
    }

    private static EnumMembersResult? ExtractPythonEnum(string filePath, string enumName)
    {
        // Python uses class-based enums, handled by the class_definition matching
        var source = File.ReadAllText(filePath);
        using var parser = Parser.Default();
        parser.SetLanguage("python");
        var tree = parser.Parse(source);
        if (tree is null) return null;
        var root = tree.RootNode();
        if (root is null) return null;

        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Kind() == "class_definition")
            {
                var nameNode = node.ChildByFieldName("name");
                if (nameNode is not null)
                {
                    var name = source[(int)nameNode.StartByte()..(int)nameNode.EndByte()];
                    if (name.Equals(enumName, StringComparison.Ordinal))
                    {
                        // Collect enum members from the body: assignments at top level
                        var members = new List<string>();
                        var body = node.ChildByFieldName("body");
                        if (body is not null)
                        {
                            var bc = body.ChildCount();
                            for (uint i = 0; i < bc; i++)
                            {
                                var child = body.Child(i);
                                if (child is null) continue;
                                if (child.Kind() == "expression_statement")
                                {
                                    var exprCount = child.ChildCount();
                                    for (uint j = 0; j < exprCount; j++)
                                    {
                                        var expr = child.Child(j);
                                        if (expr?.Kind() == "assignment")
                                        {
                                            var left = expr.ChildByFieldName("left");
                                            if (left is not null)
                                                members.Add(source[(int)left.StartByte()..(int)left.EndByte()]);
                                        }
                                    }
                                }
                            }
                        }

                        return new EnumMembersResult(
                            File: filePath,
                            EnumName: enumName,
                            StartLine: CountLine(source, (int)node.StartByte()),
                            EndLine: CountLine(source, Math.Max((int)node.StartByte(), (int)node.EndByte() - 1)),
                            Members: members.ToArray());
                    }
                }
            }

            var childCount = node.ChildCount();
            for (uint i = 0; i < childCount; i++)
            {
                var child = node.Child(i);
                if (child is not null)
                    stack.Push(child);
            }
        }

        return null;
    }

    private static int CountLine(string text, int charOffset)
    {
        var line = 1;
        for (int i = 0; i < charOffset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private readonly record struct EnumMembersResult(
        string File,
        string EnumName,
        int StartLine,
        int EndLine,
        IReadOnlyList<string> Members);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-enum-members — list enum members for a specific enum declaration

            Usage:
              ck get-enum-members <file> <EnumName>

            Supports C# (.cs), TypeScript (.ts, .tsx), Kotlin (.kt, .kts), and Python (.py) files.

            Output: JSON object with file, enum_name, start/end line, and members[].
            """);
    }
}
