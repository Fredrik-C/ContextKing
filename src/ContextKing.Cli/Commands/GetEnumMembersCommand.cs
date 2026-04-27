using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeScriptParser;

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
            var results = SupportedLanguages.IsTypeScript(filePath)
                ? ExtractTypeScript(filePath, enumName)
                : ExtractCSharp(filePath, enumName);

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

    private static EnumMembersResult? ExtractTypeScript(string filePath, string enumName)
    {
        var source = File.ReadAllText(filePath);
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();

        var stack = new Stack<TypeScriptParser.TreeSitter.TSNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.type() == "enum_declaration")
            {
                var nameNode = node.child_by_field_name("name");
                if (!nameNode.is_null())
                {
                    var name = source[(int)nameNode.start_offset()..(int)nameNode.end_offset()];
                    if (name.Equals(enumName, StringComparison.Ordinal))
                    {
                        var members = new List<string>();
                        for (uint i = 0; i < node.child_count(); i++)
                        {
                            var child = node.child(i);
                            if (child.type() != "enum_body") continue;
                            for (uint j = 0; j < child.child_count(); j++)
                            {
                                var item = child.child(j);
                                if (item.type() == "enum_assignment")
                                {
                                    var itemName = item.child_by_field_name("name");
                                    if (!itemName.is_null())
                                        members.Add(source[(int)itemName.start_offset()..(int)itemName.end_offset()]);
                                }
                            }
                        }

                        return new EnumMembersResult(
                            File: filePath,
                            EnumName: enumName,
                            StartLine: CountLine(source, (int)node.start_offset()),
                            EndLine: CountLine(source, Math.Max((int)node.start_offset(), (int)node.end_offset() - 1)),
                            Members: members.ToArray());
                    }
                }
            }

            for (uint i = 0; i < node.child_count(); i++)
                stack.Push(node.child(i));
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

            Output: JSON object with file, enum_name, start/end line, and members[].
            """);
    }
}
