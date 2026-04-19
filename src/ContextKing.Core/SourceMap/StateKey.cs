using System.Security.Cryptography;
using System.Text;

namespace ContextKing.Core.SourceMap;

/// <summary>
/// Canonical computation of the index state-key fingerprint.
/// Single responsibility: turn {branch, folder→file→hash} into a stable 16-char hex digest.
/// </summary>
/// <remarks>
/// The fingerprint changes when a file is added, removed, renamed, or modified, or
/// when the active branch changes. Content-only edits DO change the key because
/// file hashes are part of the input. Two callers share this:
/// <list type="bullet">
///   <item><c>GitTracker.ComputeStateKey</c> — fast staleness check from a fresh git enumeration.</item>
///   <item><c>SourceMapBuilder.BuildAsync</c> — final key stored alongside a rebuilt index.</item>
/// </list>
/// Both must produce identical digests for the same working tree; keeping one implementation
/// here makes that invariant structural rather than convention.
/// </remarks>
internal static class StateKey
{
    /// <summary>
    /// Computes the fingerprint from a branch name and a folder → {file → hash} map.
    /// </summary>
    public static string Compute(
        string branch,
        IReadOnlyDictionary<string, Dictionary<string, string>> folders)
    {
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (folder, files) in folders)
            foreach (var (fileName, hash) in files)
            {
                var path = folder == "." ? fileName : $"{folder}/{fileName}";
                entries.Add($"{path}:{hash}");
            }

        return Digest(branch, entries);
    }

    /// <summary>
    /// Computes the fingerprint from a branch name and a pre-built set of
    /// <c>"path:hash"</c> entries. Used on the hot staleness-check path where
    /// the git output can be consumed linearly without grouping by folder.
    /// </summary>
    public static string Compute(string branch, SortedSet<string> entries)
        => Digest(branch, entries);

    private static string Digest(string branch, SortedSet<string> entries)
    {
        var text = $"{branch}\n{string.Join('\n', entries)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }
}
