using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core;
using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;

namespace ContextKing.Cli.Commands;

internal static class GetMethodSourceCommand
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
        if (positional.Count < 2)
        {
            Error("file path and member name are required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath   = positional[0];
        var memberName = positional[1];

        if (!File.Exists(filePath))
        {
            Error($"file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        try
        {
            var results = SupportedLanguages.IsTypeScript(filePath)
                ? TsMethodSourceExtractor.Extract(filePath, memberName, typeFilter, mode)
                : MethodSourceExtractor.Extract(filePath, memberName, typeFilter, mode);

            if (results.Count == 0)
            {
                var typeHint = typeFilter is not null ? $" in type '{typeFilter}'" : string.Empty;
                Console.Error.WriteLine(
                    $"[ck get-method-source] No member '{memberName}' found{typeHint} in '{filePath}'.");

                // Suggest closest member names as a guard against guessing
                var allNames = SupportedLanguages.IsTypeScript(filePath)
                    ? TsMethodSourceExtractor.GetAllMemberNames(filePath)
                    : MethodSourceExtractor.GetAllMemberNames(filePath);

                var suggestions = allNames
                    .Where(n => n.Contains(memberName, StringComparison.OrdinalIgnoreCase)
                             || memberName.Contains(n, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.Ordinal)
                    .Take(10)
                    .ToList();

                if (suggestions.Count > 0)
                {
                    Console.Error.WriteLine($"[ck get-method-source] Did you mean: {string.Join(", ", suggestions)}");
                    Console.Error.WriteLine($"[ck get-method-source] Run 'ck signatures {filePath}' to see all member names.");
                }
                else
                {
                    Console.Error.WriteLine($"[ck get-method-source] Run 'ck signatures {filePath}' to see all available member names.");
                }

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
        => Console.Error.WriteLine($"[ck get-method-source] Error: {msg}");

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-method-source — extract a method/property body with exact span (always live)

            Usage:
              ck get-method-source <file> <member-name> [options]

            Supports C# (.cs), TypeScript (.ts), and TSX (.tsx) files.

            Options:
              --type, -t <TypeName>   Filter to a specific containing type (disambiguates overloads)
              --mode, -m <mode>       Content mode (default: signature_plus_body)

            Modes:
              signature_only          Signature only — no body
              signature_plus_body     Full member including body (default)
              body_only               Body block or expression body only
              body_without_comments   Body with all // /* */ and doc comments removed

            Output: JSON array — one object per match (multiple when overloads exist)
              [
                {
                  "file": "src/Foo.cs",
                  "member_name": "ProcessPayment",
                  "containing_type": "PaymentProcessor",
                  "signature": "public async Task<Result> ProcessPayment(PaymentRequest req)",
                  "mode": "signature_plus_body",
                  "start_line": 42,
                  "end_line": 87,
                  "start_char": 1234,
                  "end_char": 2567,
                  "content": "..."
                }
              ]

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - start_char / end_char are zero-based UTF-16 char offsets within the file.
              - For body_without_comments, spans still reflect the original body position;
                only the returned content has comments stripped.
              - Use --type to narrow when multiple members share the same name.
            """);
    }
}
