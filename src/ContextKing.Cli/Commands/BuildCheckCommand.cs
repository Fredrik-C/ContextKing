using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContextKing.Cli.Commands;

internal static class BuildCheckCommand
{
    private static readonly Regex DiagnosticRegex = new(
        @":\s*(error|warning)\s+([A-Za-z]+\d+)\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsHelp)
        {
            PrintHelp();
            return 0;
        }

        var project = reader.GetString("--project", "-p");
        var maxSet = reader.TryGetInt("--max", out var maxDiagnostics);
        if (!maxSet || maxDiagnostics <= 0) maxDiagnostics = 25;

        var configuration = reader.GetString("--configuration", "-c");
        var framework = reader.GetString("--framework", "-f");
        var runtime = reader.GetString("--runtime", "-r");
        var noRestore = reader.HasFlag("--no-restore");
        var deltaMode = reader.HasFlag("--delta");
        var resetDelta = reader.HasFlag("--reset-delta");

        var positional = reader.RemainingPositionals();
        if (string.IsNullOrWhiteSpace(project))
            project = positional.Count > 0 ? positional[0] : null;

        if (string.IsNullOrWhiteSpace(project))
        {
            Console.Error.WriteLine("[ck build-check] Error: project path is required.");
            PrintHelp();
            return 1;
        }

        project = Path.GetFullPath(project);

        var buildArgs = new List<string>
        {
            "build",
            Quote(project),
            "-v", "q"
        };
        if (!string.IsNullOrWhiteSpace(configuration)) { buildArgs.Add("-c"); buildArgs.Add(Quote(configuration)); }
        if (!string.IsNullOrWhiteSpace(framework)) { buildArgs.Add("-f"); buildArgs.Add(Quote(framework)); }
        if (!string.IsNullOrWhiteSpace(runtime)) { buildArgs.Add("-r"); buildArgs.Add(Quote(runtime)); }
        if (noRestore) buildArgs.Add("--no-restore");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = string.Join(' ', buildArgs),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = $"{stdout}\n{stderr}";

        var diagnostics = combined
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseDiagnostic)
            .Where(x => x is not null)
            .Cast<BuildDiagnostic>()
            .ToArray();

        var errors = diagnostics.Where(d => d.Kind == "error").ToArray();
        var warnings = diagnostics.Where(d => d.Kind == "warning").ToArray();

        Console.WriteLine($"[ck build-check] exit-code={process.ExitCode} errors={errors.Length} warnings={warnings.Length}");

        if (deltaMode)
        {
            var key = BuildDeltaKey(project, configuration, framework, runtime, noRestore);
            var baselinePath = GetBaselinePath(key);
            if (resetDelta && File.Exists(baselinePath))
                File.Delete(baselinePath);

            var baseline = LoadBaseline(baselinePath);
            var current = diagnostics.Select(d => d.RawLine).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var deltaRaw = current.Except(baseline.Diagnostics, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var deltaDiagnostics = diagnostics.Where(d => deltaRaw.Contains(d.RawLine)).ToArray();
            var deltaErrors = deltaDiagnostics.Where(d => d.Kind == "error").ToArray();
            var deltaWarnings = deltaDiagnostics.Where(d => d.Kind == "warning").ToArray();

            Console.WriteLine($"[ck build-check] delta errors={deltaErrors.Length} warnings={deltaWarnings.Length} (baseline={baseline.Diagnostics.Count})");
            PrintDiagnostics("delta errors", deltaErrors, maxDiagnostics);
            PrintDiagnostics("delta warnings", deltaWarnings, Math.Min(maxDiagnostics, 10));

            SaveBaseline(baselinePath, current);
        }
        else
        {
            PrintDiagnostics("errors", errors, maxDiagnostics);
            PrintDiagnostics("warnings", warnings, Math.Min(maxDiagnostics, 10));
        }

        if (diagnostics.Length == 0)
        {
            if (process.ExitCode == 0)
                Console.WriteLine("[ck build-check] Build completed with no diagnostics.");
            else
                Console.WriteLine("[ck build-check] Build failed without parseable diagnostics. Rerun plain dotnet build for full output.");
        }

        return process.ExitCode;
    }

    private static BuildDiagnostic? ParseDiagnostic(string line)
    {
        var match = DiagnosticRegex.Match(line);
        if (!match.Success) return null;

        return new BuildDiagnostic(
            Kind: match.Groups[1].Value,
            Code: match.Groups[2].Value,
            Message: match.Groups[3].Value.Trim(),
            RawLine: line.Trim());
    }

    private static void PrintDiagnostics(string label, IReadOnlyList<BuildDiagnostic> diagnostics, int max)
    {
        if (diagnostics.Count == 0) return;

        Console.WriteLine($"[ck build-check] {label}:");
        foreach (var d in diagnostics.Take(max))
            Console.WriteLine($"  {d.RawLine}");

        if (diagnostics.Count > max)
            Console.WriteLine($"  ... {diagnostics.Count - max} more {label} suppressed");
    }

    private static string Quote(string value)
        => value.Contains(' ') ? $"\"{value}\"" : value;

    private readonly record struct BuildDiagnostic(string Kind, string Code, string Message, string RawLine);

    private sealed class BuildCheckBaseline
    {
        public List<string> Diagnostics { get; init; } = [];
    }

    private static string BuildDeltaKey(
        string project,
        string? configuration,
        string? framework,
        string? runtime,
        bool noRestore)
    {
        var material = $"{project}|{configuration}|{framework}|{runtime}|{noRestore}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetBaselinePath(string key)
    {
        var root = FindRepoRootOrCurrent();
        var dir = Path.Combine(root, ".ck-index", "build-check");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{key}.json");
    }

    private static BuildCheckBaseline LoadBaseline(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new BuildCheckBaseline();
            return JsonSerializer.Deserialize<BuildCheckBaseline>(File.ReadAllText(path))
                   ?? new BuildCheckBaseline();
        }
        catch
        {
            return new BuildCheckBaseline();
        }
    }

    private static void SaveBaseline(string path, HashSet<string> diagnostics)
    {
        var payload = new BuildCheckBaseline
        {
            Diagnostics = diagnostics.Order(StringComparer.Ordinal).ToList()
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static string FindRepoRootOrCurrent()
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

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck build-check — run dotnet build with compact diagnostics output

            Usage:
              ck build-check <project.csproj> [options]
              ck build-check --project <project.csproj> [options]

            Options:
              --max <n>               Max diagnostics shown per category (default: 25)
              --configuration, -c     Build configuration (Debug/Release)
              --framework, -f         Target framework
              --runtime, -r           Target runtime identifier
              --no-restore            Skip restore
              --delta                 Show only diagnostics new since previous run for this project/options
              --reset-delta           Clear stored baseline before applying --delta
              --help, -h              Show this help

            Notes:
              - Runs dotnet build with -v q.
              - Prints concise error/warning summaries instead of full build logs.
              - Delta baseline is stored under .ck-index/build-check/.
            """);
    }
}
