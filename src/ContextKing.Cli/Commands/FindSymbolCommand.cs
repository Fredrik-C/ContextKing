using ContextKing.Core;
using ContextKing.Core.Ast;
using System.Globalization;

namespace ContextKing.Cli.Commands;

internal static class FindSymbolCommand
{
    private enum SymbolKind
    {
        Any,
        Type,
        Member
    }

    private sealed record SymbolHit(
        double Score,
        string File,
        int Line,
        string Kind,
        string Symbol,
        string Container,
        string Signature);

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
            Console.Error.WriteLine("[ck find-symbol] Error: symbol is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var query = positional[0].Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck find-symbol] Error: symbol is required.");
            return Task.FromResult(1);
        }

        var pathFlag = reader.GetString("--path");
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(pathFlag))
            roots.Add(pathFlag!);
        if (positional.Count > 1)
            roots.AddRange(positional.Skip(1));

        if (!reader.TryGetInt("--top", out var top) || top <= 0) top = 30;
        var kind = ParseKind(reader.GetString("--kind"));

        var searchRoots = SymbolSearchCommon.ResolveSearchRoots(roots);
        if (searchRoots.Count == 0)
            return Task.FromResult(1);

        var files = SymbolSearchCommon.ExpandSupportedFiles(searchRoots);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("[ck find-symbol] No supported files found in search roots.");
            return Task.FromResult(1);
        }

        var hits = new List<SymbolHit>(256);
        foreach (var file in files)
        {
            try
            {
                if (kind is SymbolKind.Any or SymbolKind.Type)
                    CollectTypeHits(file, query, hits);
                if (kind is SymbolKind.Any or SymbolKind.Member)
                    CollectMemberHits(file, query, hits);
            }
            catch
            {
                // Best effort over large repos; skip problematic files.
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
            Console.Error.WriteLine($"[ck find-symbol] No matches found for '{query}'.");
            return Task.FromResult(1);
        }

        foreach (var hit in sorted)
        {
            var score = hit.Score.ToString("0.000", CultureInfo.InvariantCulture);
            Console.WriteLine(
                $"{score}\t{SymbolSearchCommon.NormalizePath(hit.File)}:{hit.Line}\t{hit.Kind}\t{hit.Symbol}\t{hit.Container}\t{hit.Signature}");
        }

        return Task.FromResult(0);
    }

    private static void CollectTypeHits(string file, string query, List<SymbolHit> hits)
    {
        var entries = TypeHierarchyExtractor.Extract(file);
        foreach (var entry in entries)
        {
            var score = ScoreSymbol(entry.Name, query);
            if (score <= 0) continue;
            var bases = entry.BaseTypes.Count > 0 ? $" : {string.Join(", ", entry.BaseTypes)}" : string.Empty;
            hits.Add(new SymbolHit(
                Score: score + 0.01, // Nudge type hits above similarly-scored member hits.
                File: entry.File,
                Line: entry.Line,
                Kind: "type",
                Symbol: entry.Name,
                Container: "<global>",
                Signature: $"{entry.Kind} {entry.Name}{bases}".Trim()));
        }
    }

    private static void CollectMemberHits(string file, string query, List<SymbolHit> hits)
    {
        var extractor = LanguageRegistry.Get(file);
        if (extractor is null) return;

        var writer = new StringWriter();
        extractor.ExtractSignatures([file], writer);

        foreach (var line in writer.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4) continue;

            var pathLine = parts[0];
            var container = parts[1];
            var memberName = parts[2];
            var signature = parts[3];

            var qualified = $"{container}.{memberName}";
            var score = Math.Max(ScoreSymbol(memberName, query), ScoreSymbol(qualified, query));
            if (score <= 0) continue;

            if (!TryParsePathLine(pathLine, out var normalizedPath, out var lineNo))
                continue;

            hits.Add(new SymbolHit(
                Score: score,
                File: normalizedPath,
                Line: lineNo,
                Kind: "member",
                Symbol: memberName,
                Container: container,
                Signature: signature));
        }
    }

    private static SymbolKind ParseKind(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            "type" => SymbolKind.Type,
            "member" => SymbolKind.Member,
            _ => SymbolKind.Any
        };
    }

    private static bool TryParsePathLine(string value, out string file, out int line)
    {
        var idx = value.LastIndexOf(':');
        if (idx <= 0 || idx >= value.Length - 1)
        {
            file = string.Empty;
            line = 0;
            return false;
        }

        file = value[..idx];
        return int.TryParse(value[(idx + 1)..], out line);
    }

    private static double ScoreSymbol(string candidate, string query)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return 0;

        if (string.Equals(candidate, query, StringComparison.Ordinal))
            return 1.0;
        if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase))
            return 0.98;

        if (candidate.EndsWith("." + query, StringComparison.Ordinal))
            return 0.96;
        if (candidate.EndsWith("." + query, StringComparison.OrdinalIgnoreCase))
            return 0.94;

        if (candidate.StartsWith(query, StringComparison.Ordinal))
            return 0.92;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0.90;

        if (candidate.Contains(query, StringComparison.Ordinal))
            return 0.85;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 0.80;

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck find-symbol — find type/member declarations in C#, TypeScript, Kotlin, and Python files

            Usage:
              ck find-symbol <symbol> [--path <folder-or-file>] [--kind type|member] [--top <n>]
              ck find-symbol <symbol> <folder-or-file> [more paths...]

            Defaults:
              - Uses --path roots when provided.
              - Otherwise uses paths captured by latest ck find-files.
              - If no paths exist, command fails and asks for --path or find-files first.

            Output (stdout):
              <score>\t<file:line>\t<kind>\t<symbol>\t<container>\t<signature>

            Notes:
              - Score prioritizes exact and qualified-name matches.
              - Works on live disk content (uncommitted edits included).
            """);
    }
}
