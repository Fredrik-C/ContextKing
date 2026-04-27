using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TypeScriptParser;
using TypeScriptParser.TreeSitter;

namespace ContextKing.Cli.Commands;

internal static class GetTypeSourceCommand
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

        var typeFilter = reader.GetString("--kind");
        var positional = reader.RemainingPositionals();
        if (positional.Count < 2)
        {
            Console.Error.WriteLine("[ck get-type-source] Error: file path and type name are required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        var typeName = positional[1];

        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[ck get-type-source] Error: file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        try
        {
            var results = SupportedLanguages.IsTypeScript(filePath)
                ? ExtractTypeScript(filePath, typeName, typeFilter)
                : ExtractCSharp(filePath, typeName, typeFilter);

            if (results.Count == 0)
            {
                Console.Error.WriteLine($"[ck get-type-source] No type '{typeName}' found in '{filePath}'.");
                Console.WriteLine("[]");
                return Task.FromResult(1);
            }

            Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck get-type-source] Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static IReadOnlyList<TypeSourceResult> ExtractCSharp(string filePath, string typeName, string? kindFilter)
    {
        var source = File.ReadAllText(filePath);
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root = tree.GetRoot();
        var lines = tree.GetText().Lines;
        var results = new List<TypeSourceResult>();

        foreach (var node in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!node.Identifier.Text.Equals(typeName, StringComparison.Ordinal))
                continue;

            var kind = node switch
            {
                ClassDeclarationSyntax => "class",
                StructDeclarationSyntax => "struct",
                InterfaceDeclarationSyntax => "interface",
                RecordDeclarationSyntax => "record",
                EnumDeclarationSyntax => "enum",
                _ => "type"
            };

            if (!MatchesKind(kind, kindFilter))
                continue;

            var span = node.Span;
            results.Add(new TypeSourceResult(
                File: filePath,
                TypeName: typeName,
                Kind: kind,
                StartLine: lines.GetLineFromPosition(span.Start).LineNumber + 1,
                EndLine: lines.GetLineFromPosition(Math.Max(span.Start, span.End - 1)).LineNumber + 1,
                StartChar: span.Start,
                EndChar: span.End,
                Content: source[span.Start..span.End]));
        }

        return results;
    }

    private static IReadOnlyList<TypeSourceResult> ExtractTypeScript(string filePath, string typeName, string? kindFilter)
    {
        var source = File.ReadAllText(filePath);
        using var parser = new Parser();
        var tree = parser.ParseString(source);
        var root = tree.root_node();
        var results = new List<TypeSourceResult>();

        CollectTypeScriptMatches(root, source, filePath, typeName, kindFilter, results);
        return results;
    }

    private static void CollectTypeScriptMatches(
        TSNode node,
        string source,
        string filePath,
        string typeName,
        string? kindFilter,
        List<TypeSourceResult> results)
    {
        var nodeType = node.type();
        var kind = nodeType switch
        {
            "class_declaration" => "class",
            "interface_declaration" => "interface",
            "type_alias_declaration" => "type_alias",
            "enum_declaration" => "enum",
            _ => null
        };

        if (kind is not null)
        {
            var nameNode = node.child_by_field_name("name");
            if (!nameNode.is_null())
            {
                var name = source[(int)nameNode.start_offset()..(int)nameNode.end_offset()];
                if (name.Equals(typeName, StringComparison.Ordinal) && MatchesKind(kind, kindFilter))
                {
                    var start = (int)node.start_offset();
                    var end = (int)node.end_offset();
                    results.Add(new TypeSourceResult(
                        File: filePath,
                        TypeName: typeName,
                        Kind: kind,
                        StartLine: CountLine(source, start),
                        EndLine: CountLine(source, Math.Max(start, end - 1)),
                        StartChar: start,
                        EndChar: end,
                        Content: source[start..end]));
                }
            }
        }

        for (uint i = 0; i < node.child_count(); i++)
            CollectTypeScriptMatches(node.child(i), source, filePath, typeName, kindFilter, results);
    }

    private static bool MatchesKind(string kind, string? filter)
        => string.IsNullOrWhiteSpace(filter) || kind.Equals(filter, StringComparison.OrdinalIgnoreCase);

    private static int CountLine(string text, int charOffset)
    {
        var line = 1;
        for (int i = 0; i < charOffset && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private readonly record struct TypeSourceResult(
        string File,
        string TypeName,
        string Kind,
        int StartLine,
        int EndLine,
        int StartChar,
        int EndChar,
        string Content);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-type-source — extract a single type declaration source with exact span

            Usage:
              ck get-type-source <file> <TypeName> [--kind <class|interface|struct|record|enum|type_alias>]

            Output: JSON array with file, type_name, kind, start/end lines/chars, and content.
            """);
    }
}
