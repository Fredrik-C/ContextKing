using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using ContextKing.Core.Ast;

namespace ContextKing.Cli.Commands;

internal static class GetConstructorsCommand
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

        var typeFilter = reader.GetString("--type", "-t");
        var modeRaw    = reader.GetString("--mode", "-m");
        var mode       = SourceMode.SignaturePlusBody;
        if (modeRaw is not null && !TryParseMode(modeRaw, out mode))
        {
            Error($"Unknown mode '{modeRaw}'. Valid: signature_only, signature_plus_body, body_only, body_without_comments");
            return Task.FromResult(1);
        }

        var positional = reader.RemainingPositionals();
        if (positional.Count < 1)
        {
            Error("file path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        if (!File.Exists(filePath))
        {
            Error($"file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        if (!LanguageRegistry.IsSupported(filePath))
        {
            Error($"unsupported file type: '{filePath}'. Supported: {string.Join(", ", LanguageRegistry.RegisteredExtensions)}");
            return Task.FromResult(1);
        }

        try
        {
            var extractor = LanguageRegistry.Get(filePath);
            if (extractor is null)
            {
                Error($"unsupported file type: '{filePath}'. Supported: {string.Join(", ", LanguageRegistry.RegisteredExtensions)}");
                return Task.FromResult(1);
            }

            var results = extractor.ExtractAllConstructors(filePath, typeFilter, mode);

            if (results.Count == 0)
            {
                var typeHint = typeFilter is not null ? $" in type '{typeFilter}'" : string.Empty;
                Console.Error.WriteLine($"[ck get-constructors] No constructors found{typeHint} in '{filePath}'.");

                var allNames = extractor.GetAllMemberNames(filePath);

                Console.Error.WriteLine($"[ck get-constructors] All members in '{filePath}':");
                foreach (var name in allNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    Console.Error.WriteLine($"  {name}");

                Console.WriteLine("[]");
                return Task.FromResult(1);
            }

            Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Error(ex.Message);
            return Task.FromResult(1);
        }
    }

    private static bool TryParseMode(string value, out SourceMode mode)
    {
        (bool ok, mode) = value switch
        {
            "signature_only"        => (true, SourceMode.SignatureOnly),
            "signature_plus_body"   => (true, SourceMode.SignaturePlusBody),
            "body_only"             => (true, SourceMode.BodyOnly),
            "body_without_comments" => (true, SourceMode.BodyWithoutComments),
            _                       => (false, SourceMode.SignaturePlusBody)
        };
        return ok;
    }

    private static void Error(string msg)
        => Console.Error.WriteLine($"[ck get-constructors] Error: {msg}");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-constructors — extract all constructors from a C#, TypeScript, Kotlin, or Python file with exact spans

            Usage:
              ck get-constructors <file> [options]

            Supports C# (.cs), TypeScript (.ts, .tsx), Kotlin (.kt, .kts), and Python (.py) files.

            Options:
              --type, -t <TypeName>   Filter to a specific containing type (when file has multiple classes)
              --mode, -m <mode>       Content mode (default: signature_plus_body)

            Modes:
              signature_only          Signature only — no body
              signature_plus_body     Full constructor including body (default)
              body_only               Body block only
              body_without_comments   Body with all comments removed

            Output: JSON array — one object per constructor found
              [
                {
                  "file": "src/Foo.cs",
                  "member_name": "AdyenBalancePaymentGateway",
                  "containing_type": "AdyenBalancePaymentGateway",
                  "signature": "public AdyenBalancePaymentGateway(ILogger logger, IAdyenClient client)",
                  "mode": "signature_plus_body",
                  "start_line": 18,
                  "end_line": 27,
                  "start_char": 432,
                  "end_char": 687,
                  "content": "..."
                }
              ]

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - Use --type to filter when a file contains multiple classes.
              - In C#, constructor member_name equals the class name (this is normal Roslyn behaviour).
              - For TypeScript, constructor member_name is always "constructor".
            """);
    }
}
