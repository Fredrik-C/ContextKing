using ContextKing.Core;

namespace ContextKing.Cli.Commands;

internal static class ReadFullFileCommand
{
    private const int DefaultMaxLinesWithoutOverride = 300;

    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsEmpty || reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(reader.IsEmpty ? 1 : 0);
        }

        var allowLarge = reader.HasFlag("--allow-large", "--force");
        var maxLines = DefaultMaxLinesWithoutOverride;
        if (reader.TryGetInt("--max-lines", out var parsedMaxLines))
            maxLines = parsedMaxLines;

        if (maxLines < 1)
        {
            Console.Error.WriteLine("[ck read-full-file] Error: --max-lines must be >= 1.");
            return Task.FromResult(1);
        }

        var positional = reader.RemainingPositionals();
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("[ck read-full-file] Error: file path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[ck read-full-file] Error: file not found: '{filePath}'.");
            return Task.FromResult(1);
        }

        if (!SupportedLanguages.IsSupported(filePath))
        {
            Console.Error.WriteLine($"[ck read-full-file] Error: unsupported file type: '{filePath}'. Supported: .cs, .ts, .tsx");
            return Task.FromResult(1);
        }

        try
        {
            var lineCount = File.ReadLines(filePath).Count();
            if (lineCount > maxLines && !allowLarge)
            {
                Console.Error.WriteLine($"""
                    [ck read-full-file] Refused by guardrail: '{filePath}' has {lineCount} lines (> {maxLines}).

                    Prefer targeted reads first:
                      ck signatures <file>
                      ck get-method-source <file> <MemberName>
                      ck get-type-source <file> <TypeName>
                      ck get-constructors <file>
                      ck get-usings <file>
                      ck get-base-types <file>
                      ck get-enum-members <file> <EnumName>

                    If full-file context is truly required, rerun:
                      ck read-full-file --allow-large "{filePath}"
                    """);
                return Task.FromResult(1);
            }

            Console.Write(File.ReadAllText(filePath));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck read-full-file] Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            ck read-full-file — read a full source file with a built-in large-file guardrail

            Usage:
              ck read-full-file <file> [--max-lines <n>] [--allow-large]

            Options:
              --max-lines <n>   Refuse full reads above this line count unless --allow-large is set.
                                Default: {DefaultMaxLinesWithoutOverride}
              --allow-large     Override the guardrail and read the full file anyway.
              --force           Alias for --allow-large.

            Notes:
              - Supports C# (.cs), TypeScript (.ts), and TSX (.tsx).
              - Use this command when direct Read is blocked by CK guardrails.
              - Prefer targeted CK reads unless full-file context is genuinely necessary.
            """);
    }
}
