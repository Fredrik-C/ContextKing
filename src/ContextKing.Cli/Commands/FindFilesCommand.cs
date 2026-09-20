using System.Globalization;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ContextKing.Core.Embedding;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class FindFilesCommand
{
    internal static async Task<int> RunAsync(string[] args,
        Func<string, ITextEmbedder>? methodEmbedderFactory = null, CancellationToken cancellationToken = default)
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
        var taskDescription = reader.GetString("--task")?.Trim();
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

        if (string.IsNullOrWhiteSpace(taskDescription))
        {
            Console.Error.WriteLine("[ck find-files] Error: --task is required.");
            PrintHelp();
            return 1;
        }

        if (verbose && Regex.IsMatch(taskDescription, @"\b(not|except|exclude|excluding|ignore|ignoring|without|don't)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            Console.Error.WriteLine("[ck find-files] WARN: --task appears to contain an exclusion. Semantic reranking is optimized for positive retrieval intent; excluded terms may still increase similarity.");

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
                    topK: settings.FindFiles.MethodRerank ? lexicalCandidates.Count : top,
                    options: settings.FindFiles.ToSemanticOptions(),
                    mustTerms: mustTerms);
            }
            catch (Exception ex)
            {
                semanticUnavailable = true;
                Console.Error.WriteLine(
                    $"[ck find-files] WARN: semantic rerank unavailable: {ex.Message}. Falling back to lexical results.");
                selected = lexicalCandidates;
            }
        }
        else
        {
            selected = lexicalCandidates;
        }

        var methodUnavailable = false;
        var methodStatus = "disabled";
        IReadOnlyDictionary<string, FileMethodScore> methodScores = new Dictionary<string, FileMethodScore>();
        if (settings.FindFiles.MethodRerank)
        {
            var watch = Stopwatch.StartNew();
            var extraction = new MethodExtractionResult([], 0, 0);
            MethodRerankResult? scored = null;
            var methodCandidates = MethodRerankCandidateSelector.Select(lexicalCandidates);
            try
            {
                var codeEmbedder = (methodEmbedderFactory ?? CodeModelLocator.GetEmbedder)(settings.FindFiles.MethodRerankModel);
                extraction = new MethodCandidateExtractor().Extract(repoRoot, methodCandidates, query, taskDescription,
                    settings.FindFiles.ToMethodExtractionOptions(), cancellationToken);
                scored = new MethodSemanticReranker(codeEmbedder).Score(taskDescription, extraction.Cards,
                    settings.FindFiles.MaxMethodCardChars, settings.FindFiles.MaxBodyChars, settings.FindFiles.FlatMethodThreshold, cancellationToken);
                methodScores = scored.Files;
                if (scored.EmbeddedCount > 0)
                {
                    var metadata = selected.Where(h => h.SemanticScore.HasValue)
                        .ToDictionary(h => h.Path, h => h.SemanticScore!.Value, StringComparer.Ordinal);
                    selected = FileScoreFusion.Fuse(lexicalCandidates, metadata, scored, query, top,
                        settings.FindFiles.ToMethodFusionOptions()).Select(x => x.Hit).ToArray();
                    methodStatus = scored.FailureCount > 0 || extraction.Failures > 0 || scored.Cancelled
                        ? "partial" : scored.Flat ? "flat" : "active";
                }
                else if (scored.FailureCount > 0 || extraction.Failures > 0)
                {
                    methodUnavailable = true;
                    methodStatus = "unavailable";
                    selected = lexicalCandidates;
                    Console.Error.WriteLine("[ck find-files] WARN: method rerank unavailable: no method scores completed. Using lexical ranking.");
                }
                else methodStatus = scored.Cancelled ? "partial" : "active";
            }
            catch (Exception ex)
            {
                methodUnavailable = true;
                methodStatus = "unavailable";
                // Parser/model exception messages may contain source inputs.
                Console.Error.WriteLine($"[ck find-files] WARN: method rerank unavailable: {ex.GetType().Name}. Using metadata/lexical ranking.");
            }
            if (verbose)
            {
                Console.Error.WriteLine($"[ck find-files] lexical candidates: {lexicalCandidates.Count}; method rerank candidates: {methodCandidates.Count}; method candidate files: {extraction.ParsedFiles}; methods extracted: {extraction.Cards.Count}; method cards embedded: {scored?.EmbeddedCount ?? 0}");
                Console.Error.WriteLine($"[ck find-files] parse failures: {extraction.Failures}; embedding failures: {scored?.FailureCount ?? 0}; method stage duration: {watch.ElapsedMilliseconds} ms; method stage status: {methodStatus}");
                if (methodScores.Count > 0)
                    Console.Error.WriteLine(FormattableString.Invariant($"[ck find-files] method semantic range: {methodScores.Values.Min(m => m.Score):0.0000}..{methodScores.Values.Max(m => m.Score):0.0000}"));
            }
        }
        else if (verbose) Console.Error.WriteLine("[ck find-files] method stage status: disabled");

        foreach (var hit in selected.Take(top))
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
            methodScores.TryGetValue(hit.Path, out var method);
            var methodScore = methodUnavailable ? "unavailable" : method?.Score.ToString("0.0000", CultureInfo.InvariantCulture) ?? "-";
            var member = method is null ? "-" : SafeIdentifier(method.BestMember.MemberName);
            var evidence = method is null ? "-" : string.Join(',', method.BestMember.Evidence.Where(IsSafeEvidence).Take(3));
            var metadataScore = hit.SemanticScore?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "-";
            summary += $" metadata={metadataScore} method={methodScore} best_member={member} evidence={(evidence.Length == 0 ? "-" : evidence)}";
            Console.WriteLine($"{score}\t{hit.Path}\t{summary}");
        }

        return 0;
    }

    private static string SafeIdentifier(string text) =>
        string.Concat(text.Where(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '$').Take(100));

    private static bool IsSafeEvidence(string evidence)
    {
        var separator = evidence.IndexOf(':');
        return separator > 0 && evidence[..separator] is "call" or "catch" or "new" or "reference" or "attribute" or "flow"
            && MethodCandidateExtractor.SafeEvidence(evidence[..separator], evidence[(separator + 1)..]) == evidence;
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
              ck find-files "<query>" --task <text> [--must <text>] [--top <n>] [--min-score <f>] [--path <folder-or-file>] [--repo <path>] [--explain] [--verbose]
              ck find-files "<query>" --task <text> <folder-or-file> [more paths...]

            Defaults:
              - Searches repo `src/` when no path is supplied.
              - top=20, min-score=0.25
              - --must applies soft boosts (does not hard-filter to zero results)
              - --task is required reranking context; lexical search still uses <query>

              - Write --task as positive retrieval intent; exclusions such as "ignore" are not filters.
              - Local method reranking is enabled by default; set findFiles.methodRerank=false to opt out.
              - Standard installs include the code model; searches never download it.

            Output (stdout):
              <score>\t<file>
              <score>\t<file>\ttypes=<n> signatures=<n> lexical=<f> semantic=<f|unavailable> matched=<terms>   (with --explain)
              --explain also adds metadata, method, best_member, and up to three evidence identifiers.
            """);
    }
}
