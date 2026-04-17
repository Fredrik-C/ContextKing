using ContextKing.Core.Ast;
using System.Text.RegularExpressions;

namespace ContextKing.Cli.Commands;

internal static class SignaturesCommand
{
    internal static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            PrintHelp();
            return Task.FromResult(args.Length == 0 ? 1 : 0);
        }

        // All non-flag arguments are treated as file paths or glob patterns.
        // We expand globs here so behavior is consistent across shells
        // (PowerShell does not always expand globs for native executables).
        var inputs = args.Where(a => !a.StartsWith('-')).ToList();

        if (inputs.Count == 0)
        {
            Console.Error.WriteLine("[ck signatures] Error: at least one file path is required.");
            return Task.FromResult(1);
        }

        var expanded = new List<string>();
        foreach (var input in inputs)
        {
            if (IsGlob(input))
            {
                var matches = ExpandGlob(input);
                if (matches.Count == 0)
                {
                    Console.Error.WriteLine($"[ck signatures] WARN: no files matched pattern: '{input}'");
                    continue;
                }

                expanded.AddRange(matches);
            }
            else
            {
                if (Directory.Exists(input))
                {
                    var directoryMatches = Directory
                        .EnumerateFiles(input, "*.cs", SearchOption.AllDirectories)
                        .ToList();

                    if (directoryMatches.Count == 0)
                    {
                        Console.Error.WriteLine($"[ck signatures] WARN: no .cs files found in directory: '{input}'");
                        continue;
                    }

                    expanded.AddRange(directoryMatches);
                }
                else
                {
                    expanded.Add(input);
                }
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

        // Always live — reads directly from disk, no cache
        SignatureExtractor.Extract(valid, Console.Out, Console.Error);

        // Hint: if the folder contains only small files, suggest reading directly next time.
        EmitSmallFolderHint(valid);

        return Task.FromResult(0);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck signatures — extract method/property signatures from C# files (live, no cache)

            Usage:
              ck signatures <folder/>              — all .cs files in the folder (recursive)
              ck signatures <file.cs> [file2.cs …] — specific files
              ck signatures <pattern/*.cs>          — glob pattern

            The folder form is the recommended starting point: pipe the folder path returned
            by 'ck find-scope' directly into 'ck signatures' to get every method in that
            subtree without needing to enumerate files yourself.

            Output (stdout):
              <filepath>:<line>\t<containingType>\t<memberName>\t<signature>
              One line per method, constructor, or property.

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - No index required; works without running 'ck index'.
              - Use before reading full file content when evaluating multiple candidates.
              - Supports glob patterns (for example: src/**/Services/*.cs).
            """);
    }

    private static bool IsGlob(string value)
        => value.IndexOfAny(['*', '?', '[', ']']) >= 0;

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
            {
                totalLines += File.ReadLines(f).Count();
            }

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

    private static List<string> ExpandGlob(string pattern)
    {
        var fullPattern = Path.GetFullPath(pattern);
        var fullPatternNorm = fullPattern.Replace('\\', '/');

        var firstWildcardIndex = fullPatternNorm.IndexOfAny(['*', '?', '[', ']']);
        if (firstWildcardIndex < 0)
            return [pattern];

        var slashBeforeWildcard = fullPatternNorm.LastIndexOf('/', firstWildcardIndex);
        var root = slashBeforeWildcard <= 0
            ? Path.GetPathRoot(fullPattern) ?? Directory.GetCurrentDirectory()
            : fullPatternNorm[..slashBeforeWildcard];

        root = root.Replace('/', Path.DirectorySeparatorChar);
        if (!Directory.Exists(root))
            return [];

        var regex = GlobToRegex(fullPatternNorm);
        var matches = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => regex.IsMatch(path.Replace('\\', '/')))
            .Select(path =>
            {
                var rel = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
                return rel.StartsWith("..") ? path : rel;
            })
            .ToList();

        return matches;
    }

    private static Regex GlobToRegex(string pattern)
    {
        // Convert a normalized glob pattern to regex:
        // ** -> any depth, * -> any non-separator chars, ? -> single non-separator char.
        const string DoubleStarToken = "__CK_DOUBLESTAR__";
        var rx = Regex.Escape(pattern)
            .Replace(@"\*\*", DoubleStarToken)
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]")
            .Replace(DoubleStarToken, ".*");

        return new Regex($"^{rx}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
