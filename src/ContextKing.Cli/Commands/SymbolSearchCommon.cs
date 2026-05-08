using System.Text.Json.Nodes;
using ContextKing.Core;

namespace ContextKing.Cli.Commands;

internal static class SymbolSearchCommon
{
    internal static IReadOnlyList<string> ResolveSearchRoots(IEnumerable<string> explicitRoots)
    {
        var roots = explicitRoots
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count > 0)
            return roots;

        var scoped = LoadScopedFolders();
        if (scoped.Count > 0)
            return scoped;

        Console.Error.WriteLine(
            "[ck] Error: no search roots resolved. " +
            "Pass --path <folder> or run ck find-files first.");
        return [];
    }

    internal static IReadOnlyList<string> ExpandSupportedFiles(IReadOnlyList<string> roots)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                if (SupportedLanguages.IsSupported(root))
                    files.Add(root);
                continue;
            }

            if (!Directory.Exists(root))
                continue;

            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (SupportedLanguages.IsSupported(file))
                    files.Add(file);
            }
        }

        return files.ToArray();
    }

    internal static string NormalizePath(string p) => p.Replace('\\', '/');

    private static IReadOnlyList<string> LoadScopedFolders()
    {
        try
        {
            var repoRoot = GitRootOrCurrent();
            var statePath = Path.Combine(repoRoot, ".ck-index", ".ck-guard-state.json");
            if (!File.Exists(statePath))
                return [];

            var node = JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject;
            if (node?["scopedFolders"] is not JsonArray arr)
                return [];

            var folders = new List<string>();
            foreach (var item in arr)
            {
                var folder = item?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(folder))
                    continue;

                folders.Add(folder);
            }

            return folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string GitRootOrCurrent()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 20; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
