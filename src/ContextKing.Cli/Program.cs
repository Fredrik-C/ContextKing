using ContextKing.Cli.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

if (args[0] is "--version")
    return PrintVersion();

// ck init is exempt — it creates .ck.json, so version-checking before init runs is wrong.
if (args[0] is not "init")
{
    var initError = CheckRepositoryInitialized();
    if (initError is not null)
    {
        Console.Error.WriteLine(initError);
        return 1;
    }

    var versionError = CheckVersionRequirement();
    if (versionError is not null)
    {
        Console.Error.WriteLine(versionError);
        return 2;
    }
}

return args[0] switch
{
    "init"               => await InitCommand.RunAsync(args[1..]),
    "index"              => await IndexCommand.RunAsync(args[1..]),
    "find-files"         => await FindFilesCommand.RunAsync(args[1..]),
    "get-keyword-map"    => await GetKeywordMapCommand.RunAsync(args[1..]),
    "expand-folder"      => await ExpandFolderCommand.RunAsync(args[1..]),
    "signatures"         => await SignaturesCommand.RunAsync(args[1..]),
    "find-symbol"        => await FindSymbolCommand.RunAsync(args[1..]),
    "refs"               => await RefsCommand.RunAsync(args[1..]),
    "get-method-source"  => await GetMethodSourceCommand.RunAsync(args[1..]),
    "get-type-source"    => await GetTypeSourceCommand.RunAsync(args[1..]),
    "get-enum-members"   => await GetEnumMembersCommand.RunAsync(args[1..]),
    "get-constructors"   => await GetConstructorsCommand.RunAsync(args[1..]),
    "get-usings"         => await GetUsingsCommand.RunAsync(args[1..]),
    "get-base-types"     => await GetBaseTypesCommand.RunAsync(args[1..]),
    "read-full-file"     => await ReadFullFileCommand.RunAsync(args[1..]),
    "build-check"        => await BuildCheckCommand.RunAsync(args[1..]),
    "recall"             => await RecallCommand.RunAsync(args[1..]),
    "learn"              => await LearnCommand.RunAsync(args[1..]),
    "forget"             => await ForgetCommand.RunAsync(args[1..]),
    _ => PrintError($"Unknown command: '{args[0]}'. Run 'ck --help' for usage.")
};

// ── Version requirement check ──────────────────────────────────────────────────
static string? CheckVersionRequirement()
{
    var candidate = FindCkConfigPath();
    if (candidate is null) return null;

    try
    {
        var json = File.ReadAllText(candidate);
        var match = System.Text.RegularExpressions.Regex.Match(
            json, @"""minVersion""\s*:\s*""([^""]+)""");
        if (match.Success)
        {
            var required = match.Groups[1].Value;
            if (CompareVersions(Program.Version, required) < 0)
                return $"[ck] Error: this repository requires ck >= {required} (installed: {Program.Version}).\n" +
                       "Upgrade with: curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.sh | bash";
        }
    }
    catch { /* malformed .ck.json — ignore */ }

    return null;
}

static string? CheckRepositoryInitialized()
{
    if (FindCkConfigPath() is not null) return null;
    return "[ck] Error: this repository is not initialized for Context King.\n" +
           "Run 'ck init' from the repository root, then retry your command.";
}

static string? FindCkConfigPath()
{
    var dir = Directory.GetCurrentDirectory();
    for (int depth = 0; depth < 20; depth++)
    {
        var candidate = Path.Combine(dir, ".ck.json");
        if (File.Exists(candidate)) return candidate;
        var parent = Path.GetDirectoryName(dir);
        if (parent is null || parent == dir) break;
        dir = parent;
    }
    return null;
}

static int CompareVersions(string a, string b)
{
    int[] Parse(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    var av = Parse(a);
    var bv = Parse(b);
    int len = Math.Max(av.Length, bv.Length);
    for (int i = 0; i < len; i++)
    {
        var ai = i < av.Length ? av[i] : 0;
        var bi = i < bv.Length ? bv[i] : 0;
        if (ai != bi) return ai.CompareTo(bi);
    }
    return 0;
}

// ── Helpers ────────────────────────────────────────────────────────────────────
static void PrintHelp()
{
    Console.WriteLine($"""
        ck — Context King: semantic code navigation for large C#, TypeScript, Kotlin, and Python codebases

        Commands:
          ck init                Initialize Context King in the current git repository
          ck index               Build or update the semantic source-map index
          ck find-files          Source discovery over path/type/member names
          ck get-keyword-map     Inspect related indexed keywords for each query term
          ck expand-folder       List files in a folder with filtered signatures
          ck signatures          Extract method signatures from source files (always live)
          ck find-symbol         Find type/member declarations in source files
          ck refs                Find textual references of a symbol in scoped code folders
          ck get-method-source   Extract method/property source with exact span (always live)
          ck get-type-source     Extract a single type declaration source with exact span (always live)
          ck get-enum-members    List enum members (always live)
          ck get-constructors    Extract all constructors from a file with exact spans (always live)
          ck get-usings          List all using directives / import statements in a file (always live)
          ck get-base-types      List type declarations with base classes and interfaces (always live)
          ck read-full-file      Read a full source file with built-in large-file guardrail
          ck build-check         Run dotnet build with compact diagnostics output
          ck recall              Retrieve institutional knowledge (by folder or query)
          ck learn               Record a new knowledge snippet
          ck forget              Remove a stale knowledge snippet by ID

        Run 'ck <command> --help' for command-specific options.

        Version: {Program.Version}
        """);
}

static int PrintVersion()
{
    Console.WriteLine($"ck {Program.Version}");
    return 0;
}

static int PrintError(string message)
{
    Console.Error.WriteLine($"[ck] Error: {message}");
    return 1;
}

// ── Version constant (single source of truth) ─────────────────────────────────
static partial class Program
{
    internal const string Version = "1.8.8";
}
