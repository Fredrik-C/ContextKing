using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;
using ContextKing.Core.Embedding;
using ContextKing.Core.Git;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Builds and incrementally maintains the source-map index.
/// Responsibility: orchestrate git enumeration → token generation → embedding → index storage.
/// All SQLite access is delegated to <see cref="SourceMapIndex"/>.
/// </summary>
public sealed class SourceMapBuilder(BgeEmbedder embedder, string[]? excludeSegments = null)
{
    private static readonly string[] DefaultExclusions = ["Test", "Tests", "Specs"];
    private readonly string[] _excludeSegments = excludeSegments ?? DefaultExclusions;

    // ── Public API ────────────────────────────────────────────────────────────

    public static string GetDbPath(string worktreeRoot) =>
        SourceMapIndex.DbPathFor(worktreeRoot);

    /// <summary>
    /// Checks whether the index is fresh, stale, or missing.
    /// Staleness is detected by comparing the stored fingerprint against the current
    /// working-tree state from git. The fingerprint covers the active branch name,
    /// the set of source filenames, and their content hashes, so it changes when files
    /// are added/removed/renamed/modified or when the active branch changes.
    /// </summary>
    public static IndexStatus GetStatus(
        string worktreeRoot,
        IReadOnlyList<string>? excludeSegments = null)
    {
        var dbPath = SourceMapIndex.DbPathFor(worktreeRoot);
        var index  = new SourceMapIndex(dbPath);

        if (!index.Exists) return IndexStatus.Missing;

        try
        {
            var stored = index.ReadMeta("index_state_key");
            if (string.IsNullOrEmpty(stored)) return IndexStatus.Stale;

            var current = GitTracker.ComputeStateKey(worktreeRoot, excludeSegments);
            return string.Equals(stored, current, StringComparison.Ordinal)
                ? IndexStatus.Fresh
                : IndexStatus.Stale;
        }
        catch
        {
            return IndexStatus.Stale;
        }
    }

    /// <summary>
    /// Builds or incrementally updates the index for <paramref name="repoRoot"/>.
    /// Re-embeds folders whose file content changed (add/remove/rename/modify).
    /// Public method names from source files are extracted and included as lexical
    /// keywords in the folder embedding.
    /// Progress messages go to <paramref name="progress"/> (stderr by convention).
    /// </summary>
    public async Task BuildAsync(
        string repoRoot,
        bool forceRebuild = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var dbPath = SourceMapIndex.DbPathFor(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var index = new SourceMapIndex(dbPath);
        index.EnsureSchema();

        if (forceRebuild) index.ClearAllFolders();

        var gitFolders = GitTracker.ListSourceFilesByFolder(repoRoot, _excludeSegments);
        progress?.Report($"Found {gitFolders.Count} leaf folders to index.");

        // ── Classify each folder: re-embed or skip ────────────────────────────

        var existing = index.LoadFolderStates();

        // Snapshot folder list for parallel processing
        var folderEntries = gitFolders.ToArray();
        var results       = new FolderRow?[folderEntries.Length];
        int skipped       = 0;
        int embedded      = 0;
        int degree        = Math.Max(1, Environment.ProcessorCount);

        // ── Parallel: classify → tokenize → Roslyn extract → embed ───────────
        // All four steps are independent per folder and safe to run concurrently.
        // File I/O, Roslyn parsing, and ONNX inference all benefit from parallelism.

        await Task.Run(() =>
        {
            Parallel.For(0, folderEntries.Length,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                i =>
                {
                    var (folderPath, files) = folderEntries[i];
                    var fileHashesJson = SerialiseHashes(files);

                    if (!forceRebuild && existing.TryGetValue(folderPath, out var stored)
                        && stored.FileHashes == fileHashesJson)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var filenameSetKey = FilenameSetKey(files);
                    var pathTokens     = PathTokenizer.TokenizePath(folderPath);

                    // Extract public method names from all source files (Roslyn/tree-sitter parse — CPU + I/O)
                    var methodNames  = ExtractPublicMethodNames(repoRoot, folderPath, files.Keys);

                    // Match tokens: lowercase, globally deduplicated across path + filenames + method words.
                    // Deduplication prevents a token that appears in both the folder path and a filename
                    // from being double-counted in the exact-match fraction.
                    var combined = BuildDistinctTokens(pathTokens, files.Keys, methodNames);

                    // Embedding text: readable phrase for BGE semantic embedding.
                    // Preserves casing and word structure for better embedding quality.
                    var pathPhrase   = PathTokenizer.PathToPhrase(folderPath);
                    var filePhrase   = string.Join(", ", files.Keys.Select(PathTokenizer.FileNameToPhrase));
                    var methodPhrase = methodNames.Count > 0
                        ? ". Methods: " + string.Join(", ", methodNames.Select(PathTokenizer.MethodNameToPhrase))
                        : string.Empty;
                    var embeddingText = $"{pathPhrase}. Files: {filePhrase}{methodPhrase}".Trim();

                    var blob     = SourceMapIndex.EncodeEmbedding(embedder.Embed(embeddingText));
                    results[i]   = new FolderRow(folderPath, combined, embeddingText, blob, fileHashesJson, filenameSetKey);

                    int done = Interlocked.Increment(ref embedded);
                    if (done % 50 == 0 || done == folderEntries.Length - Volatile.Read(ref skipped))
                        progress?.Report(
                            $"  {Volatile.Read(ref skipped) + done}/{gitFolders.Count} folders indexed " +
                            $"({done} updated, {Volatile.Read(ref skipped)} unchanged).");
                });
        }, ct);

        // ── Persist results ───────────────────────────────────────────────────

        var rows = results.Where(r => r is not null).ToArray()!;
        index.UpsertFolders(rows!);
        index.DeleteFolders(gitFolders.Keys.ToHashSet(StringComparer.Ordinal));

        var stateKey = ComputeStateKeyFromFolders(gitFolders, repoRoot);
        index.WriteMeta("index_state_key", stateKey);
        index.WriteMeta("git_head",        GitTracker.GetHead(repoRoot));
        index.WriteMeta("indexed_at",      DateTime.UtcNow.ToString("O"));

        progress?.Report($"Index complete: {embedded} updated, {skipped} unchanged.");
    }

    // ── Pure computation helpers ──────────────────────────────────────────────

    /// <summary>
    /// Builds a globally-deduplicated, space-separated lowercase token string
    /// covering path segments, filenames, and public method name words.
    /// A token that appears in more than one section is emitted only once,
    /// preserving the first-occurrence order (path → filenames → methods).
    /// </summary>
    private static string BuildDistinctTokens(
        string pathTokens,
        IEnumerable<string> fileNames,
        IReadOnlyList<string> methodNames)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Add(string token)
        {
            if (seen.Add(token)) result.Add(token);
        }

        foreach (var t in pathTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Add(t);

        foreach (var fileName in fileNames)
            foreach (var t in PathTokenizer.TokenizeFileName(fileName)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Add(t);

        foreach (var name in methodNames)
            foreach (var t in PathTokenizer.MethodNameToPhrase(name)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.ToLowerInvariant()))
                Add(t);

        return string.Join(' ', result);
    }

    /// <summary>
    /// Extracts distinct public method names from all source files in a folder.
    /// Dispatches to the appropriate extractor based on file extension.
    /// Names are returned as-is (not split by camelCase) for use as exact-match keywords.
    /// </summary>
    private static IReadOnlyList<string> ExtractPublicMethodNames(
        string repoRoot,
        string folderPath,
        IEnumerable<string> fileNames)
    {
        var seen  = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();

        foreach (var fileName in fileNames)
        {
            var relPath = folderPath == "."
                ? fileName
                : $"{folderPath}/{fileName}";
            var absPath = Path.Combine(
                repoRoot,
                relPath.Replace('/', Path.DirectorySeparatorChar));

            var extracted = IsTypeScriptFile(fileName)
                ? TsPublicMethodNameExtractor.ExtractFromFile(absPath)
                : PublicMethodNameExtractor.ExtractFromFile(absPath);

            foreach (var name in extracted)
            {
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static bool IsTypeScriptFile(string path) =>
        path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);

    private static string SerialiseHashes(Dictionary<string, string> files)
        => JsonSerializer.Serialize(
            files.OrderBy(f => f.Key, StringComparer.Ordinal)
                 .ToDictionary(f => f.Key, f => f.Value));

    /// <summary>Sorted pipe-delimited list of filenames — the re-embed trigger key.</summary>
    private static string FilenameSetKey(Dictionary<string, string> files)
        => string.Join('|', files.Keys.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Derives a fingerprint covering branch name, file paths, and content hashes.
    /// Changes when files are added/removed/renamed OR when file content is modified.
    /// </summary>
    private static string ComputeStateKeyFromFolders(
        Dictionary<string, Dictionary<string, string>> folders,
        string repoRoot)
    {
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (folder, files) in folders)
            foreach (var (fileName, hash) in files)
            {
                var path = folder == "." ? fileName : $"{folder}/{fileName}";
                entries.Add($"{path}:{hash}");
            }

        var branch = GitTracker.GetBranch(repoRoot);
        var text   = $"{branch}\n{string.Join('\n', entries)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
