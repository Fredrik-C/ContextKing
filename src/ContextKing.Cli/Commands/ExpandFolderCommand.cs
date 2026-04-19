using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;
using System.Text.RegularExpressions;

namespace ContextKing.Cli.Commands;

/// <summary>
/// ck expand-folder — lists all source files in a folder with their signatures,
/// optionally filtered by a regex pattern. Files that have no matching signatures
/// are excluded from output.
/// </summary>
internal static class ExpandFolderCommand
{
    internal static Task<int> RunAsync(string[] args)
    {
        string? pattern    = null;
        string? folderPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pattern" when i + 1 < args.Length:
                    pattern = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return Task.FromResult(0);
                default:
                    if (!args[i].StartsWith('-'))
                        folderPath = args[i];
                    break;
            }
        }

        if (folderPath is null)
        {
            Console.Error.WriteLine("[ck expand-folder] Error: folder path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        folderPath = folderPath.TrimEnd('/', '\\');

        if (!Directory.Exists(folderPath))
        {
            Console.Error.WriteLine($"[ck expand-folder] Error: directory not found: '{folderPath}'");
            return Task.FromResult(1);
        }

        Regex? filterRegex = null;
        if (pattern is not null)
        {
            // Normalize shell-escaped alternation: agents often write "Foo\|Bar" in bash
            // double-quoted strings. In .NET regex \| means a literal pipe, not alternation.
            // Treat \| as | so the intuitive meaning works.
            var normalizedPattern = pattern.Replace(@"\|", "|");
            try
            {
                filterRegex = new Regex(normalizedPattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"[ck expand-folder] Error: invalid pattern '{pattern}': {ex.Message}");
                return Task.FromResult(1);
            }
        }

        var allFiles = Directory
            .EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedSourceFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allFiles.Count == 0)
        {
            Console.Error.WriteLine($"[ck expand-folder] No supported source files found in '{folderPath}'");
            return Task.FromResult(0);
        }

        // Capture all signature output into a string buffer
        var csFiles = allFiles.Where(f => f.EndsWith(".cs",  StringComparison.OrdinalIgnoreCase)).ToList();
        var tsFiles = allFiles.Where(IsTypeScriptFile).ToList();

        var captured = new StringWriter();
        if (csFiles.Count > 0)
            SignatureExtractor.Extract(csFiles, captured, Console.Error);
        if (tsFiles.Count > 0)
            TsSignatureExtractor.Extract(tsFiles, captured, Console.Error);

        // Parse captured lines and group by normalised file path.
        // Line format: filepath:line\tcontainingType\tmemberName\tsignature
        var byFile = new Dictionary<string, List<(int lineNo, string rest)>>(StringComparer.Ordinal);

        foreach (var raw in captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;

            var firstTab = line.IndexOf('\t');
            if (firstTab < 0) continue;

            var location = line[..firstTab];          // "filepath:line"
            var rest     = line[(firstTab + 1)..];    // "containingType\tmemberName\tsignature"

            var lastColon = location.LastIndexOf(':');
            if (lastColon <= 0) continue;

            var filePath = location[..lastColon];
            if (!int.TryParse(location[(lastColon + 1)..], out var lineNum)) continue;

            if (!byFile.TryGetValue(filePath, out var entries))
                byFile[filePath] = entries = [];

            entries.Add((lineNum, rest));
        }

        // Emit results in file-enumeration order, applying the optional filter
        bool anyOutput = false;
        foreach (var file in allFiles)
        {
            var normalized = file.Replace('\\', '/');
            if (!byFile.TryGetValue(normalized, out var entries) || entries.Count == 0)
                continue;

            // Apply pattern filter against "containingType\tmemberName\tsignature"
            var matched = filterRegex is null
                ? entries
                : entries.Where(e => filterRegex.IsMatch(e.rest)).ToList();

            if (matched.Count == 0)
                continue;

            if (anyOutput)
                Console.WriteLine();

            Console.WriteLine(normalized);
            foreach (var (lineNo, rest) in matched)
                Console.WriteLine($"  {lineNo}\t{rest}");

            anyOutput = true;
        }

        if (!anyOutput)
        {
            // Print to stdout (not stderr) so the agent sees this even when stderr is suppressed.
            Console.WriteLine(
                filterRegex is not null
                    ? $"[ck expand-folder] No signatures matched pattern '{pattern}' in '{folderPath}' ({allFiles.Count} files scanned)"
                    : $"[ck expand-folder] No signatures found in '{folderPath}'");
        }

        return Task.FromResult(0);
    }

    private static bool IsSupportedSourceFile(string path)
        => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsTypeScriptFile(path);

    private static bool IsTypeScriptFile(string path)
        => path.EndsWith(".ts",  StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck expand-folder — list files in a folder with their matching signatures

            Usage:
              ck expand-folder [--pattern <regex>] <folder>

            Arguments:
              <folder>            Path to the folder to expand (recursive)

            Options:
              --pattern <regex>   Case-insensitive regex to filter signatures.
                                  Matched against containingType, memberName, and signature text.
                                  Files with no matching signatures are excluded from output.
                                  If omitted, all signatures in all files are shown.
              --help, -h          Show this help

            Output (stdout):
              <file-path>
                <line>  <containingType>  <memberName>  <signature>

              One block per file that has at least one matching signature.

            Examples:
              ck expand-folder src/Modules/Payment/Adyen/
              ck expand-folder --pattern "Refund" src/Modules/Payment/Adyen/
              ck expand-folder --pattern "async Task" src/Modules/Payment/
            """);
    }
}
