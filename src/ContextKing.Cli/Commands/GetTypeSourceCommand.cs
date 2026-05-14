using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;

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
            IReadOnlyList<TypeSourceResult> results;
            if (SupportedLanguages.IsTypeScript(filePath))
            {
                var lang = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? "tsx" : "typescript";
                results = ExtractTreeSitter(filePath, typeName, typeFilter, lang);
            }
            else if (SupportedLanguages.IsKotlin(filePath))
                results = ExtractTreeSitter(filePath, typeName, typeFilter, "kotlin");
            else if (SupportedLanguages.IsPython(filePath))
                results = ExtractTreeSitter(filePath, typeName, typeFilter, "python");
            else
                results = ExtractCSharp(filePath, typeName, typeFilter);

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

    private static IReadOnlyList<TypeSourceResult> ExtractTreeSitter(string filePath, string typeName, string? kindFilter, string languageName)
    {
        var source = File.ReadAllText(filePath);
        using var parser = Parser.Default();
        parser.SetLanguage(languageName);
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];
        var results = new List<TypeSourceResult>();

        CollectTreeSitterMatches(root, source, filePath, typeName, kindFilter, languageName, results);
        return results;
    }

    private static void CollectTreeSitterMatches(
        Node node,
        string source,
        string filePath,
        string typeName,
        string? kindFilter,
        string languageName,
        List<TypeSourceResult> results)
    {
        var nodeKind = node.Kind();

        var kindMap = languageName switch
        {
            "typescript" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["class_declaration"] = "class",
                ["interface_declaration"] = "interface",
                ["type_alias_declaration"] = "type_alias",
                ["enum_declaration"] = "enum"
            },
            "kotlin" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["class_declaration"] = "class",
                ["object_declaration"] = "object",
                ["interface_declaration"] = "interface",
                ["enum_class"] = "enum"
            },
            "python" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["class_definition"] = "class"
            },
            _ => new Dictionary<string, string>(StringComparer.Ordinal)
        };

        if (kindMap.TryGetValue(nodeKind, out var kind))
        {
            var nameNode = node.ChildByFieldName("name");
            if (nameNode is not null)
            {
                var name = source[(int)nameNode.StartByte()..(int)nameNode.EndByte()];
                if (name.Equals(typeName, StringComparison.Ordinal) && MatchesKind(kind, kindFilter))
                {
                    var start = (int)node.StartByte();
                    var end = (int)node.EndByte();
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

        var childCount = node.ChildCount();
        for (uint i = 0; i < childCount; i++)
        {
            var child = node.Child(i);
            if (child is not null)
                CollectTreeSitterMatches(child, source, filePath, typeName, kindFilter, languageName, results);
        }
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

            Supports C# (.cs), TypeScript (.ts, .tsx), Kotlin (.kt, .kts), and Python (.py) files.

            Output: JSON array with file, type_name, kind, start/end lines/chars, and content.
            """);
    }
}
