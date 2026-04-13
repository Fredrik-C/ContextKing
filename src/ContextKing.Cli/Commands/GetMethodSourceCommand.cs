using System.Text.Json;
using System.Text.Json.Serialization;
using ContextKing.Core.Ast;

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
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return Task.FromResult(args.Length == 0 ? 1 : 0);
        }

        var positional = new List<string>();
        string? typeFilter = null;
        var mode = SourceMode.SignaturePlusBody;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--type" or "-t":
                    if (i + 1 >= args.Length)
                    {
                        Error("--type requires a value.");
                        return Task.FromResult(1);
                    }
                    typeFilter = args[++i];
                    break;

                case "--mode" or "-m":
                    if (i + 1 >= args.Length)
                    {
                        Error("--mode requires a value.");
                        return Task.FromResult(1);
                    }
                    if (!TryParseMode(args[++i], out mode))
                    {
                        Error($"Unknown mode '{args[i]}'. Valid: signature_only, signature_plus_body, body_only, body_without_comments");
                        return Task.FromResult(1);
                    }
                    break;

                default:
                    if (!args[i].StartsWith('-'))
                        positional.Add(args[i]);
                    break;
            }
        }

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
            var results = MethodSourceExtractor.Extract(filePath, memberName, typeFilter, mode);

            if (results.Count == 0)
            {
                var typeHint = typeFilter is not null ? $" in type '{typeFilter}'" : string.Empty;
                Console.Error.WriteLine(
                    $"[ck get-method-source] No member '{memberName}' found{typeHint} in '{filePath}'.");
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
              ck get-method-source <file.cs> <member-name> [options]

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
