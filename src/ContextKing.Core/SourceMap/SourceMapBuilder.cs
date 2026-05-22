using System.Text.Json;
using ContextKing.Core.Ast;
using ContextKing.Core.Embedding;
using ContextKing.Core.Git;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Builds and incrementally maintains the source-map index.
/// Responsibility: orchestrate git enumeration → token generation → embedding → index storage.
/// All SQLite access is delegated to <see cref="SourceMapIndex"/>.
/// </summary>
public sealed class SourceMapBuilder(string[]? excludeSegments = null)
{
    private static readonly string[] DefaultExclusions = ["Test", "Tests", "Specs"];
    private const string SchemaVersion = "3";
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
            if (index.ReadMeta("source_map_schema_version") != SchemaVersion) return IndexStatus.Stale;

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

        if (forceRebuild)
        {
            index.ClearAllFolders();
            index.ClearAllFiles();
        }

        var gitFolders = GitTracker.ListSourceFilesByFolder(repoRoot, _excludeSegments);
        progress?.Report($"Found {gitFolders.Count} leaf folders to index.");
        var totalSw = Stopwatch.StartNew();
        var scanSw = Stopwatch.StartNew();

        // ── Classify each folder: refresh changed files only ──────────────────
        var existingFiles = index.LoadFileStates();

        // Snapshot folder list for changed-file discovery
        var folderEntries = gitFolders.ToArray();
        int skipped       = 0;
        int embedded      = 0;
        int parsed        = 0;
        var fileRows = new ConcurrentBag<FileRow>();
        var changedFiles = new List<ChangedFileWorkItem>(capacity: Math.Max(256, existingFiles.Count));

        foreach (var (folderPath, files) in folderEntries)
        {
            var folderUpdated = false;
            foreach (var (fileName, fileHash) in files)
            {
                var relPath = folderPath == "."
                    ? fileName
                    : $"{folderPath}/{fileName}";
                if (!forceRebuild
                    && existingFiles.TryGetValue(relPath, out var storedFile)
                    && storedFile.FileHash == fileHash)
                    continue;

                folderUpdated = true;
                changedFiles.Add(new ChangedFileWorkItem(folderPath, relPath, fileHash));
            }

            if (!folderUpdated)
                skipped++;
        }
        scanSw.Stop();

        progress?.Report($"Found {changedFiles.Count} changed files to index.");

        // ── Parallel two-stage pipeline: parse → embed ────────────────────────
        // Stage A (parse) and stage B (embed) are decoupled via a bounded queue,
        // which keeps embedders saturated while avoiding unbounded memory growth.

        var parseDegree = Math.Max(1, Environment.ProcessorCount);
        var embedWorkers = Math.Max(1, Environment.ProcessorCount / 2);
        var parsedQueue = new BlockingCollection<ParsedFileDoc>(boundedCapacity: 2048);
        var pipelineSw = Stopwatch.StartNew();
        long parseTicks = 0;
        long embedTicks = 0;

        var embedTasks = Enumerable.Range(0, embedWorkers)
            .Select(workerIndex => Task.Run(() =>
            {
                foreach (var doc in parsedQueue.GetConsumingEnumerable(ct))
                {
                    var embedItemSw = Stopwatch.StartNew();
                    try
                    {
                        fileRows.Add(new FileRow(
                            doc.RelPath,
                            doc.FolderPath,
                            doc.EmbeddingText,
                            doc.FileHash,
                            doc.TypeNames,
                            doc.MethodNames,
                            doc.TypeCount,
                            doc.SignatureCount));

                        var doneFiles = Interlocked.Increment(ref embedded);
                        if (doneFiles % 500 == 0)
                            progress?.Report($"  {doneFiles} files embedded ({Volatile.Read(ref parsed)} parsed).");
                    }
                    catch
                    {
                        // Skip malformed or transiently failing files and continue indexing.
                    }
                    finally
                    {
                        embedItemSw.Stop();
                        Interlocked.Add(ref embedTicks, embedItemSw.ElapsedTicks);
                    }
                }
            }, ct))
            .ToArray();

        await Task.Run(() =>
        {
            Parallel.ForEach(
                changedFiles,
                new ParallelOptions { MaxDegreeOfParallelism = parseDegree, CancellationToken = ct },
                item =>
                {
                    var parseItemSw = Stopwatch.StartNew();
                    if (TryExtractFileDoc(repoRoot, item, out var doc))
                    {
                        parsedQueue.Add(doc, ct);
                        var doneParsed = Interlocked.Increment(ref parsed);
                        if (doneParsed % 500 == 0)
                            progress?.Report($"  {doneParsed} files parsed.");
                    }
                    parseItemSw.Stop();
                    Interlocked.Add(ref parseTicks, parseItemSw.ElapsedTicks);
                });
        }, ct);
        parsedQueue.CompleteAdding();
        await Task.WhenAll(embedTasks);
        pipelineSw.Stop();

        // ── Persist results ───────────────────────────────────────────────────
        var persistSw = Stopwatch.StartNew();

        // File-first mode: do not maintain folder rows.
        index.ClearAllFolders();
        index.UpsertFiles(fileRows.ToArray());
        var filePathsToKeep = gitFolders
            .SelectMany(kvp => kvp.Value.Keys.Select(file => kvp.Key == "." ? file : $"{kvp.Key}/{file}"))
            .ToHashSet(StringComparer.Ordinal);
        index.DeleteFiles(filePathsToKeep);

        var stateKey = StateKey.Compute(GitTracker.GetBranch(repoRoot), gitFolders);
        index.WriteMeta("index_state_key", stateKey);
        index.WriteMeta("git_head",        GitTracker.GetHead(repoRoot));
        index.WriteMeta("indexed_at",      DateTime.UtcNow.ToString("O"));
        index.WriteMeta("source_map_schema_version", SchemaVersion);
        persistSw.Stop();
        totalSw.Stop();
        var parseMs = (long)(parseTicks * 1000.0 / Stopwatch.Frequency);
        var embedMs = (long)(embedTicks * 1000.0 / Stopwatch.Frequency);

        progress?.Report($"Index complete: {embedded} files updated, {skipped} folders unchanged.");
        progress?.Report(
            $"Timing: scan={scanSw.ElapsedMilliseconds}ms, " +
            $"parse+embed={pipelineSw.ElapsedMilliseconds}ms, " +
            $"parse={parseMs}ms, embed={embedMs}ms, " +
            $"persist={persistSw.ElapsedMilliseconds}ms, total={totalSw.ElapsedMilliseconds}ms.");
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
        IReadOnlyList<string> symbolNames)
    {
        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Add(string token)
        {
            if (!IsUsefulCombinedToken(token))
                return;
            if (seen.Add(token)) result.Add(token);
        }

        foreach (var t in pathTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Add(t);

        foreach (var fileName in fileNames)
            foreach (var t in PathTokenizer.TokenizeFileName(fileName)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                Add(t);

        foreach (var name in symbolNames)
            foreach (var t in PathTokenizer.MethodNameToPhrase(name)
                         .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(x => x.ToLowerInvariant()))
                Add(t);

        return string.Join(' ', result);
    }

    private static bool IsUsefulCombinedToken(string token)
    {
        if (token.Length < 3 || token.Length > 48)
            return false;
        if (!token.Any(char.IsLetter))
            return false;
        if (LowRankDictionary.Contains(token))
            return false;

        return true;
    }

    /// <summary>
    /// Extracts distinct public surface names from all source files in a folder.
    /// Dispatches to the appropriate extractor based on file extension.
    /// Names are returned as-is (not split by camelCase) for use as exact-match keywords.
    /// </summary>
    private static IReadOnlyList<string> ExtractPublicSymbolNames(
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

            var extracted = LanguageRegistry.Get(absPath)
                ?.ExtractPublicNamesFromFile(absPath) ?? [];

            foreach (var name in extracted)
            {
                if (seen.Add(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static string SerialiseHashes(Dictionary<string, string> files)
        => JsonSerializer.Serialize(
            files.OrderBy(f => f.Key, StringComparer.Ordinal)
                 .ToDictionary(f => f.Key, f => f.Value));

    /// <summary>Sorted pipe-delimited list of filenames — the re-embed trigger key.</summary>
    private static string FilenameSetKey(Dictionary<string, string> files)
        => string.Join('|', files.Keys.Order(StringComparer.OrdinalIgnoreCase));

    private static bool TryExtractFileDoc(
        string repoRoot,
        ChangedFileWorkItem item,
        out ParsedFileDoc doc)
    {
        doc = default;
        try
        {
            var absPath = Path.Combine(repoRoot, item.RelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(absPath))
                return false;

            IReadOnlyList<string> distinctMethodNames;
            IReadOnlyList<string> distinctTypeNames;

            var extractResult = LanguageRegistry.Get(absPath)?.ExtractTypeAndMethodNames(absPath);
            if (extractResult is null)
                return false;

            distinctTypeNames = extractResult.Value.TypeNames;
            distinctMethodNames = extractResult.Value.MethodNames;
            var typeCount = distinctTypeNames.Count;
            var signatureCount = distinctMethodNames.Count;
            if (typeCount == 0 && signatureCount == 0)
                return false;

            var fileName = Path.GetFileNameWithoutExtension(item.RelPath);
            var pathContext = item.FolderPath == "." ? "<root>" : item.FolderPath.Replace('\\', '/');
            var joinedTypeNames = string.Join(';', distinctTypeNames);
            var joinedMethodNames = string.Join(';', distinctMethodNames);
            var embeddingText = $"Path: {pathContext}. File: {fileName}.";
            doc = new ParsedFileDoc(
                item.RelPath,
                item.FolderPath,
                item.FileHash,
                embeddingText,
                joinedTypeNames,
                joinedMethodNames,
                typeCount,
                signatureCount);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct ChangedFileWorkItem(
        string FolderPath,
        string RelPath,
        string FileHash);

    private readonly record struct ParsedFileDoc(
        string RelPath,
        string FolderPath,
        string FileHash,
        string EmbeddingText,
        string TypeNames,
        string MethodNames,
        int TypeCount,
        int SignatureCount);
}
