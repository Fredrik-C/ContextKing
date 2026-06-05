using System.Globalization;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class FindFilesCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return 0;
        }

        if (!reader.TryGetInt("--top", out var top) || top <= 0) top = 20;
        if (!reader.TryGetFloat("--min-score", out var minScore)) minScore = 0.25f;
        var explain = reader.HasFlag("--explain");
        var verbose = reader.HasFlag("--verbose");
        var taskDescription = reader.GetString("--task");
        var mustTerms = reader.GetStringList("--must");
        var repo = reader.GetString("--repo");
        _ = reader.HasFlag("--quiet");
        var positional = reader.RemainingPositionals();
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("[ck find-files] Error: query is required.");
            PrintHelp();
            return 1;
        }

        var query = positional[0].Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("[ck find-files] Error: query is required.");
            return 1;
        }

        var roots = new List<string>();
        var pathFlag = reader.GetString("--path");
        if (!string.IsNullOrWhiteSpace(pathFlag))
            roots.Add(pathFlag!);
        if (positional.Count > 1)
            roots.AddRange(positional.Skip(1));

        string repoRoot;
        try
        {
            repoRoot = GitTracker.GetWorktreeRoot(repo);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck find-files] Error: {ex.Message}");
            return 1;
        }

        if (roots.Count == 0)
        {
            var srcRoot = Path.Combine(repoRoot, "src");
            roots.Add(Directory.Exists(srcRoot) ? srcRoot : repoRoot);
        }

        var normalizedRoots = roots
            .Select(x => NormalizeRootToRelative(x, repoRoot))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedRoots.Length == 0)
        {
            Console.Error.WriteLine("[ck find-files] No valid roots found.");
            return 1;
        }

        var status = SourceMapBuilder.GetStatus(repoRoot);
        if (status != IndexStatus.Fresh)
        {
            if (verbose)
            {
                Console.Error.WriteLine(
                    status == IndexStatus.Missing
                        ? "[ck find-files] No index found — building now (first-time setup)..."
                        : "[ck find-files] Index is stale — refreshing...");
            }

            var builder = new SourceMapBuilder();
            var progress = verbose
                ? new Progress<string>(msg => Console.Error.WriteLine($"[ck find-files] {msg}"))
                : null;
            await builder.BuildAsync(repoRoot, false, progress);
        }

        var dbPath = SourceMapBuilder.GetDbPath(repoRoot);
        var settings = CkSettings.Load(repoRoot, verbose);
        var lexicalTopK = settings.FindFiles.OverfetchTopK(top);
        var searcher = new FileMapSearcher();
        var lexicalCandidates = searcher.SearchHits(
            dbPath,
            query,
            topK: lexicalTopK,
            minScore: minScore,
            allowedFolders: normalizedRoots,
            mustTerms: mustTerms);

        if (lexicalCandidates.Count == 0)
        {
            Console.Error.WriteLine("[ck find-files] No matches found.");
            return 1;
        }

        IReadOnlyList<FileSearchHit> selected;
        var semanticUnavailable = false;
        if (settings.FindFiles.SemanticRerank)
        {
            try
            {
                using var embedder = ModelLocator.CreateEmbedder();
                selected = new CandidateSemanticReranker(embedder).Rerank(
                    lexicalQuery: query,
                    taskDescription: taskDescription,
                    lexicalCandidates: lexicalCandidates,
                    topK: top,
                    options: settings.FindFiles.ToSemanticOptions(),
                    mustTerms: mustTerms);
            }
            catch (Exception ex)
            {
                semanticUnavailable = true;
                Console.Error.WriteLine(
                    $"[ck find-files] WARN: semantic rerank unavailable: {ex.Message}. Falling back to lexical results.");
                selected = lexicalCandidates.Take(top).ToArray();
            }
        }
        else
        {
            selected = lexicalCandidates.Take(top).ToArray();
        }

        foreach (var hit in selected)
        {
            var score = hit.Score.ToString("0.0000", CultureInfo.InvariantCulture);
            if (!explain)
            {
                Console.WriteLine($"{score}\t{hit.Path}");
                continue;
            }

            var lexical = hit.LexicalScore.ToString("0.0000", CultureInfo.InvariantCulture);
            var semantic = semanticUnavailable
                ? "unavailable"
                : hit.SemanticScore?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "-";
            var matched = hit.MatchedTerms.Count == 0 ? "-" : string.Join(",", hit.MatchedTerms);
            var summary = $"types={hit.TypeCount} signatures={hit.SignatureCount} lexical={lexical} semantic={semantic} matched={matched}";
            Console.WriteLine($"{score}\t{hit.Path}\t{summary}");
        }

        return 0;
    }

    private static string NormalizeRootToRelative(string root, string repoRoot)
    {
        if (Path.IsPathRooted(root))
            root = Path.GetRelativePath(repoRoot, root);

        var normalized = SymbolSearchCommon.NormalizePath(root);
        if (normalized.StartsWith('/'))
        {
            normalized = SymbolSearchCommon.NormalizePath(
                Path.GetRelativePath(repoRoot, root));
        }
        return normalized.TrimEnd('/');
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck find-files — lexical file retrieval from path/type/method names

            Usage:
              ck find-files "<query>" [--task <text>] [--must <text>] [--top <n>] [--min-score <f>] [--path <folder-or-file>] [--repo <path>] [--explain] [--verbose]
              ck find-files "<query>" <folder-or-file> [more paths...]

            Defaults:
              - Searches repo `src/` when no path is supplied.
              - top=20, min-score=0.25
              - --must applies soft boosts (does not hard-filter to zero results)
              - --task adds optional reranking context; lexical search still uses <query>

            Output (stdout):
              <score>\t<file>
              <score>\t<file>\ttypes=<n> signatures=<n> lexical=<f> semantic=<f|unavailable> matched=<terms>   (with --explain)
            """);
    }
}
