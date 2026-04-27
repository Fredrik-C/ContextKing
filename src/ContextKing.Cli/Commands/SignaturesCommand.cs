using ContextKing.Core;
using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;
using ContextKing.Cli.KeywordAtlas;
using ContextKing.Core.SourceMap;
using System.Text.RegularExpressions;

namespace ContextKing.Cli.Commands;

internal static class SignaturesCommand
{
    private const int MaxDirectoryFilesWithoutAll = 30;

    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsEmpty || reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(reader.IsEmpty ? 1 : 0);
        }

        var allowLargeDirectory = reader.HasFlag("--all");

        // All non-flag arguments are treated as file paths or glob patterns.
        // Globs are expanded here so behavior is consistent across shells
        // (PowerShell does not always expand globs for native executables).
        var inputs = reader.RemainingPositionals();

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("[ck signatures] Error: at least one file path is required.");
            return Task.FromResult(1);
        }

        var expanded = new List<string>();
        foreach (var input in inputs)
        {
            if (GlobMatcher.IsGlob(input))
            {
                var matches = GlobMatcher.Expand(input);
                if (matches.Count == 0)
                {
                    Console.Error.WriteLine($"[ck signatures] WARN: no files matched pattern: '{input}'");
                    continue;
                }

                expanded.AddRange(matches);
            }
            else if (Directory.Exists(input))
            {
                var directoryMatches = Directory
                    .EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
                    .Where(SupportedLanguages.IsSupported)
                    .ToList();

                if (directoryMatches.Count == 0)
                {
                    Console.Error.WriteLine($"[ck signatures] WARN: no supported source files found in directory: '{input}'");
                    continue;
                }

                if (!allowLargeDirectory && directoryMatches.Count > MaxDirectoryFilesWithoutAll)
                {
                    var ranked = SmartRankFiles(input, directoryMatches);
                    var selected = SelectAdaptiveSubset(ranked, directoryMatches.Count);
                    Console.Error.WriteLine(
                        $"[ck hint] Large folder ({directoryMatches.Count} files). " +
                        $"Smart-ranked subset selected ({selected.Count} files, {Math.Round(100.0 * selected.Count / directoryMatches.Count)}% of files) " +
                        "using folder-specific lexical signal and session keyword atlas relevance. " +
                        "Use --all for full output.");
                    expanded.AddRange(selected);
                    continue;
                }

                expanded.AddRange(directoryMatches);
            }
            else
            {
                expanded.Add(input);
            }
        }

        // Validate: warn about non-existent paths but continue with the rest.
        var valid = new List<string>();
        foreach (var path in expanded.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path))
                valid.Add(path);
            else
                Console.Error.WriteLine($"[ck signatures] WARN: file not found: '{path}'");
        }

        if (valid.Count == 0)
        {
            Console.Error.WriteLine("[ck signatures] No valid files to process.");
            return Task.FromResult(1);
        }

        // Always live — reads directly from disk, no cache.
        // Split files by language and dispatch to the appropriate extractor.
        var csFiles = valid.Where(SupportedLanguages.IsCSharp).ToList();
        var tsFiles = valid.Where(SupportedLanguages.IsTypeScript).ToList();

        if (csFiles.Count > 0)
            SignatureExtractor.Extract(csFiles, Console.Out, Console.Error);
        if (tsFiles.Count > 0)
            TsSignatureExtractor.Extract(tsFiles, Console.Out, Console.Error);

        // Guard: warn when many explicit files/globs were processed.
        if (valid.Count > 30)
        {
            Console.Error.WriteLine(
                $"[ck hint] {valid.Count} files processed — this is a broad folder. " +
                "Prefer 'ck expand-folder --pattern \"<keyword>\" <folder>' unless you intentionally need all signatures. " +
                "For large folders, signatures now applies adaptive relevance ranking unless --all is set.");
        }

        // Hint: if the folder contains only small files, suggest reading directly next time.
        EmitSmallFolderHint(valid);

        return Task.FromResult(0);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck signatures — extract method/property signatures from C# and TypeScript files (live, no cache)

            Usage:
              ck signatures <folder/>              — all supported files in the folder (recursive)
              ck signatures <file.cs> [file2.ts …]  — specific files (.cs, .ts, .tsx)
              ck signatures <pattern/*.ts>          — glob pattern
              ck signatures --all <folder/>          — allow broad folder output intentionally

            Prefer 'ck expand-folder --pattern "<keyword>" <folder>' when you have a keyword — it is faster and more focused.
            For large folders (>30 files), signatures applies adaptive relevance ranking by default to limit output volume.
            Pass --all to force full output intentionally.

            Output (stdout):
              <filepath>:<line>\t<containingType>\t<memberName>\t<signature>
              One line per method, constructor, property, or function.

            Supported languages:
              - C# (.cs)        — uses Roslyn for full-fidelity parsing
              - TypeScript (.ts, .tsx) — uses tree-sitter for AST extraction

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - No index required; works without running 'ck index'.
              - Use before reading full file content when evaluating multiple candidates.
              - Supports glob patterns (for example: src/**/Services/*.cs).
            """);
    }

    /// <summary>
    /// When all processed files are small (≤50 lines avg), emit a stderr hint suggesting
    /// the agent read the files directly next time instead of running signatures first.
    /// </summary>
    private static void EmitSmallFolderHint(List<string> files)
    {
        if (files.Count == 0 || files.Count > 20)
            return;

        try
        {
            var totalLines = 0L;
            foreach (var f in files)
                totalLines += File.ReadLines(f).Count();

            var avgLines = totalLines / files.Count;
            if (avgLines <= 50)
            {
                Console.Error.WriteLine(
                    $"[ck hint] This folder has {files.Count} files averaging {avgLines} lines — " +
                    "consider reading files directly with Read next time instead of running signatures first.");
            }
        }
        catch
        {
            // Best-effort hint — don't fail the command.
        }
    }

    private static IReadOnlyList<ScoredFile> SmartRankFiles(string folder, IReadOnlyList<string> files)
    {
        var atlas = SessionKeywordAtlasStore.LoadForFolder(folder);
        var focusTerms = (atlas?.HighValueTerms ?? [])
            .Where(IsUsefulTerm)
            .ToHashSet(StringComparer.Ordinal);

        var fileTokens = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var tokens = TokenizeForRanking(rel);
            fileTokens[file] = tokens;

            foreach (var token in tokens)
            {
                if (!documentFrequency.TryAdd(token, 1))
                    documentFrequency[token]++;
            }
        }

        var totalDocs = Math.Max(1, files.Count);
        var scored = new List<ScoredFile>(files.Count);
        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(folder, file).Replace('\\', '/');
            var tokens = fileTokens[file];
            var score = 0d;
            var focusMatches = 0;

            foreach (var token in tokens)
            {
                if (!documentFrequency.TryGetValue(token, out var df) || df <= 0) continue;
                var rarity = Math.Log(1.0 + (double)totalDocs / df);
                score += rarity;
                if (focusTerms.Contains(token))
                {
                    focusMatches++;
                    score += rarity * 2.5;
                }
            }

            if (focusMatches > 0)
                score += focusMatches * 2.0;

            if (rel.Contains("/test", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/tests", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/temporary", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("/migration", StringComparison.OrdinalIgnoreCase))
            {
                score -= 2.0;
            }

            // Slightly favor implementation files over interfaces in huge folders.
            if (Path.GetFileNameWithoutExtension(rel).StartsWith("I", StringComparison.Ordinal) &&
                Path.GetFileNameWithoutExtension(rel).Length > 2)
            {
                score -= 0.5;
            }

            scored.Add(new ScoredFile(file, rel, score));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SelectAdaptiveSubset(IReadOnlyList<ScoredFile> ranked, int originalCount)
    {
        if (ranked.Count <= MaxDirectoryFilesWithoutAll)
            return ranked.Select(x => x.Path).ToArray();

        var topScore = Math.Max(0.0001, ranked[0].Score);
        var minScoreThreshold = topScore * 0.28;
        var floor = Math.Min(Math.Max(12, MaxDirectoryFilesWithoutAll), ranked.Count);
        var cap = Math.Clamp((int)Math.Ceiling(Math.Sqrt(originalCount) * 8), 24, 120);
        var totalSignal = ranked.Sum(x => Math.Max(0.01, x.Score));

        var selected = new List<ScoredFile>(Math.Min(cap, ranked.Count));
        var cumulative = 0d;
        foreach (var item in ranked)
        {
            var score = Math.Max(0.01, item.Score);
            var coverage = totalSignal <= 0 ? 1d : cumulative / totalSignal;

            var keep =
                selected.Count < floor
                || item.Score >= minScoreThreshold
                || coverage < 0.85;

            if (!keep)
                break;

            selected.Add(item);
            cumulative += score;

            if (selected.Count >= cap)
            {
                // Allow ties just beyond the adaptive cap to avoid hard top-N cutoff behavior.
                var next = selected.Count < ranked.Count ? ranked[selected.Count].Score : double.NegativeInfinity;
                if (Math.Abs(next - item.Score) > 0.02)
                    break;
            }
        }

        return selected
            .Select(x => x.Path)
            .ToArray();
    }

    private static HashSet<string> TokenizeForRanking(string relativePath)
    {
        var tokens = PathTokenizer.TokenizeQuery(relativePath)
            .Where(IsUsefulTerm)
            .ToHashSet(StringComparer.Ordinal);

        var normalized = Regex.Replace(relativePath, @"[^\p{L}\p{Nd}_]+", " ");
        foreach (var token in PathTokenizer.TokenizeQuery(normalized))
        {
            if (IsUsefulTerm(token))
                tokens.Add(token);
        }

        return tokens;
    }

    private static bool IsUsefulTerm(string token)
        => token.Length >= 3
           && token.Any(char.IsLetter)
           && !LowRankDictionary.Contains(token);

    private readonly record struct ScoredFile(string Path, string RelativePath, double Score);
}
