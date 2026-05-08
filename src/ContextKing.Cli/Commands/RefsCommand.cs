using System.Text.RegularExpressions;
using System.Globalization;

namespace ContextKing.Cli.Commands;

internal static class RefsCommand
{
    private sealed record RefHit(double Score, string File, int Line, string Snippet);

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
            Console.Error.WriteLine("[ck refs] Error: symbol is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var symbol = positional[0].Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Console.Error.WriteLine("[ck refs] Error: symbol is required.");
            return Task.FromResult(1);
        }

        var pathFlag = reader.GetString("--path");
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(pathFlag))
            roots.Add(pathFlag!);
        if (positional.Count > 1)
            roots.AddRange(positional.Skip(1));

        if (!reader.TryGetInt("--top", out var top) || top <= 0) top = 100;
        var ignoreCase = reader.HasFlag("--ignore-case");

        var searchRoots = SymbolSearchCommon.ResolveSearchRoots(roots);
        if (searchRoots.Count == 0)
            return Task.FromResult(1);

        var files = SymbolSearchCommon.ExpandSupportedFiles(searchRoots);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("[ck refs] No supported files found in search roots.");
            return Task.FromResult(1);
        }

        var identifier = ExtractIdentifier(symbol);
        var regexOptions = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var identifierRegex = new Regex(@"\b" + Regex.Escape(identifier) + @"\b", regexOptions);

        var hits = new List<RefHit>(256);
        foreach (var file in files)
        {
            try
            {
                var lineNo = 0;
                foreach (var rawLine in File.ReadLines(file))
                {
                    lineNo++;
                    var line = rawLine ?? string.Empty;
                    if (!identifierRegex.IsMatch(line))
                        continue;

                    var score = 0.7;
                    if (line.Contains(symbol, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        score = 1.0;
                    else if (line.Contains(identifier, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        score = 0.85;

                    var snippet = line.Trim();
                    if (snippet.Length > 220)
                        snippet = snippet[..220] + "...";

                    hits.Add(new RefHit(
                        Score: score,
                        File: file,
                        Line: lineNo,
                        Snippet: snippet));
                }
            }
            catch
            {
                // Best effort over large repos.
            }
        }

        var sorted = hits
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Line)
            .Take(top)
            .ToArray();

        if (sorted.Length == 0)
        {
            Console.Error.WriteLine($"[ck refs] No references found for '{symbol}'.");
            return Task.FromResult(1);
        }

        foreach (var hit in sorted)
        {
            var score = hit.Score.ToString("0.000", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{score}\t{SymbolSearchCommon.NormalizePath(hit.File)}:{hit.Line}\t{hit.Snippet}");
        }

        return Task.FromResult(0);
    }

    private static string ExtractIdentifier(string symbol)
    {
        var trimmed = symbol.Trim();
        var parts = trimmed.Split(['.', ':'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? trimmed : parts[^1];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck refs — find textual references for a symbol in C# and TypeScript/TSX files

            Usage:
              ck refs <symbol> [--path <folder-or-file>] [--top <n>] [--ignore-case]
              ck refs <symbol> <folder-or-file> [more paths...]

            Defaults:
              - Uses --path roots when provided.
              - Otherwise uses paths captured by latest ck find-files.
              - If no paths exist, command fails and asks for --path or find-files first.

            Output (stdout):
              <score>\t<file:line>\t<line snippet>

            Notes:
              - Finds identifier-boundary matches of the symbol (or its right-most segment).
              - Works on live disk content (uncommitted edits included).
            """);
    }
}
