using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using ContextKing.Core.Ast;

namespace ContextKing.Cli.Commands;

internal static class GetBaseTypesCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull
    };

    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsEmpty || reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(reader.IsEmpty ? 1 : 0);
        }

        var positional = reader.RemainingPositionals();
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("[ck get-base-types] Error: file path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[ck get-base-types] Error: file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        if (!SupportedLanguages.IsSupported(filePath))
        {
            Console.Error.WriteLine($"[ck get-base-types] Error: unsupported file type: '{filePath}'. Supported: {string.Join(", ", LanguageRegistry.RegisteredExtensions)}");
            return Task.FromResult(1);
        }

        try
        {
            var entries = TypeHierarchyExtractor.Extract(filePath);
            Console.WriteLine(JsonSerializer.Serialize(entries, JsonOptions));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck get-base-types] Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-base-types — extract class/interface declarations with their base types from a file

            Usage:
              ck get-base-types <file>

            Supports C# (.cs), TypeScript (.ts, .tsx), Kotlin (.kt, .kts), and Python (.py) files.

            Output: JSON array — one object per type declaration found
              [
                {
                  "file": "src/Foo.cs",
                  "name": "AdyenBalancePaymentGateway",
                  "kind": "class",
                  "base_types": ["PaymentGatewayBase", "IPaymentGateway", "IDisposable"],
                  "line": 12
                }
              ]

            Fields:
              name        — type name
              kind        — "class", "abstract class", "interface", "struct", "record",
                            "record struct", "enum"
              base_types  — all entries from the base list; base class and interfaces are not
                            distinguished (requires compilation for that)
              line        — 1-based line number of the declaration

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - base_types is empty [] when the type has no base class or interfaces.
              - For C# enums, base_types contains the underlying type if specified (e.g. "byte").
              - For TypeScript and Kotlin, both extends and implements entries appear in base_types.
            """);
    }
}
