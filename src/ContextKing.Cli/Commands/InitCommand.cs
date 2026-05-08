using System.Text.Json;
using System.Text.Json.Nodes;
using ContextKing.Core.Git;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli.Commands;

internal static class InitCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        bool force   = args.Contains("--force");
        bool quiet   = args.Contains("--quiet") || args.Contains("-q");
        bool migrate = args.Contains("--migrate");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("""
                ck init — Initialize Context King in a git repository

                Usage:
                  ck init [--force] [--quiet] [--migrate]

                Options:
                  --force     Overwrite existing .ck.json even if it already exists
                  --quiet     Suppress informational output
                  --migrate   Remove legacy per-repo deploy artifacts (binary, model, hooks,
                              rules, settings entries) so the global install takes over

                What it does:
                  1. Detects and reports any legacy per-repo deployment (ck used to be
                     deployed directly into .claude/, .codex/, .opencode/, .agents/)
                  2. With --migrate: removes those legacy assets cleanly
                  3. Creates .ck.json at the repo root with the minimum required version
                  4. Adds .ck-index/ to .gitignore (machine-local, never commit)
                  5. Creates .ck-knowledge/ directory (commit this to share knowledge)
                  6. Registers a Bash(*) wildcard in .claude/settings.local.json so all
                     ck commands run without approval prompts (global install only)

                .ck.json fields:
                  "minVersion"  Minimum ck version required in this repo
                  "brain"       Knowledge capture (learn/recall/forget). Default: true.
                                Set to false to disable all brain commands in this repo.

                The global tool (binary, hooks, skills) is installed separately:
                  curl -fsSL https://raw.githubusercontent.com/Fredrik-C/ContextKing/main/scripts/install-global.sh | bash
                """);
            return 0;
        }

        string repoRoot;
        try
        {
            repoRoot = GitTracker.GetWorktreeRoot();
        }
        catch
        {
            Console.Error.WriteLine("[ck] Error: not inside a git repository. Run 'ck init' from within a git repo.");
            return 1;
        }

        void Print(string msg)  { if (!quiet) Console.WriteLine(msg); }
        void Warn(string msg)   { Console.WriteLine(msg); }  // warnings always shown

        // ── Detect legacy per-repo deployment ─────────────────────────────────
        var legacyRoots = DetectLegacyDeployments(repoRoot);

        if (legacyRoots.Count > 0)
        {
            Warn("");
            Warn("[ck] Legacy per-repo deployment detected:");
            foreach (var (label, paths) in legacyRoots)
            {
                Warn($"  {label}:");
                foreach (var p in paths)
                    Warn($"    {Path.GetRelativePath(repoRoot, p)}");
            }
            Warn("");

            if (migrate)
            {
                Warn("[ck] --migrate: removing legacy assets...");
                await MigrateLegacyDeployments(repoRoot, legacyRoots, Print);
                Warn("");
            }
            else
            {
                Warn("  These assets are managed globally since the global install.");
                Warn("  Re-run with --migrate to remove them from this repository.");
                Warn("  Without --migrate they are harmless but waste repo space.");
                Warn("");
            }
        }

        bool anyChange = false;

        // ── Create .ck.json ───────────────────────────────────────────────────
        var ckJsonPath = Path.Combine(repoRoot, ".ck.json");
        var ckJsonExists = File.Exists(ckJsonPath);
        var triggerIndexBuild = !ckJsonExists || force;
        if (!ckJsonExists || force)
        {
            await File.WriteAllTextAsync(ckJsonPath, $$"""
                {
                  "minVersion": "{{Program.Version}}",
                  "brain": true
                }
                """);
            Print($"  Created .ck.json (minVersion: {Program.Version}, brain: true)");
            anyChange = true;
        }
        else
        {
            Print("  .ck.json already exists — skipped (use --force to overwrite)");
        }

        // ── Add .ck-index/ to .gitignore ──────────────────────────────────────
        var gitignorePath = Path.Combine(repoRoot, ".gitignore");
        string gitignoreContent = File.Exists(gitignorePath) ? await File.ReadAllTextAsync(gitignorePath) : "";
        if (!gitignoreContent.Contains(".ck-index"))
        {
            await File.AppendAllTextAsync(gitignorePath,
                "\n# Context King index (machine-local, never commit)\n.ck-index/\n");
            Print("  Added .ck-index/ to .gitignore");
            anyChange = true;
        }

        // ── Create .ck-knowledge/ with .gitkeep ──────────────────────────────
        var knowledgeDir = Path.Combine(repoRoot, ".ck-knowledge");
        if (!Directory.Exists(knowledgeDir))
        {
            Directory.CreateDirectory(knowledgeDir);
            await File.WriteAllTextAsync(Path.Combine(knowledgeDir, ".gitkeep"), "");
            Print("  Created .ck-knowledge/ (commit this directory to share knowledge with your team)");
            anyChange = true;
        }

        var kgIgnorePath = Path.Combine(knowledgeDir, ".gitignore");
        string kgIgnoreContent = File.Exists(kgIgnorePath) ? await File.ReadAllTextAsync(kgIgnorePath) : "";
        if (!kgIgnoreContent.Contains(".postsession-offset"))
        {
            await File.AppendAllTextAsync(kgIgnorePath, ".postsession-offset\n");
            anyChange = true;
        }

        // ── Register Claude Code permissions ──────────────────────────────────
        // ck init writes a single wildcard to settings.local.json so every ck
        // command runs without an approval prompt.
        var claudeDir = Path.Combine(repoRoot, ".claude");
        if (Directory.Exists(claudeDir))
        {
            var binaryPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(binaryPath) && IsGlobalInstallBinary(binaryPath))
            {
                var settingsLocal = Path.Combine(claudeDir, "settings.local.json");
                if (await RegisterClaudePermissionAsync(settingsLocal, binaryPath))
                {
                    Print($"  Registered Bash({binaryPath} *) in .claude/settings.local.json");
                    anyChange = true;
                }
            }
        }

        // ── Build index on first init / --force ───────────────────────────────
        // This eager initial build makes the first find-files call faster.
        if (triggerIndexBuild)
        {
            try
            {
                if (!quiet)
                    Console.WriteLine("  Building semantic index (.ck-index/index.db)...");

                var builder = new SourceMapBuilder();
                var progress = quiet
                    ? null
                    : new Progress<string>(msg => Console.WriteLine($"  [index] {msg}"));
                await builder.BuildAsync(repoRoot, forceRebuild: false, progress);

                if (!quiet)
                    Console.WriteLine("  Index build complete.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ck] Error: index build failed during init: {ex.Message}");
                return 1;
            }
        }

        if (!anyChange && legacyRoots.Count == 0)
        {
            Print("  Repository already initialized — nothing to do.");
            return 0;
        }

        if (anyChange)
        {
            Print("");
            Print("Done. Commit the new files to share the configuration with your team:");
            var filesToAdd = new List<string> { ".ck.json" };
            if (!gitignoreContent.Contains(".ck-index")) filesToAdd.Add(".gitignore");
            filesToAdd.Add(".ck-knowledge/");
            Print($"  git add {string.Join(" ", filesToAdd)}");
            Print("  git commit -m 'chore: initialize Context King'");
        }

        return 0;
    }

    // ── Permission registration ────────────────────────────────────────────────

    // Returns true for global install paths (not a per-repo skills/ck/ deploy).
    private static bool IsGlobalInstallBinary(string binaryPath)
        => !binaryPath.Contains("/skills/ck/") && !binaryPath.Contains("\\skills\\ck\\");

    // Adds "Bash(<binaryPath> *)" to permissions.allow in settings.local.json.
    // Returns true if the file was changed, false if the entry was already present.
    private static async Task<bool> RegisterClaudePermissionAsync(
        string settingsPath, string binaryPath)
    {
        var permission = $"Bash({binaryPath} *)";

        var json = File.Exists(settingsPath)
            ? await File.ReadAllTextAsync(settingsPath)
            : "{}";

        JsonObject root;
        try   { root = JsonNode.Parse(json)?.AsObject() ?? new JsonObject(); }
        catch { root = new JsonObject(); }

        if (!root.ContainsKey("permissions"))
            root["permissions"] = new JsonObject();
        var perms = root["permissions"]!.AsObject();

        if (!perms.ContainsKey("allow"))
            perms["allow"] = new JsonArray();
        var allow = perms["allow"]!.AsArray();

        if (allow.Any(n => n?.GetValue<string>() == permission))
            return false;

        allow.Add(permission);

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(settingsPath, root.ToJsonString(opts));
        return true;
    }

    // ── Legacy detection ───────────────────────────────────────────────────────

    // Returns a list of (CLI label, list of paths that exist) for each detected legacy deployment.
    private static List<(string Label, List<string> Paths)> DetectLegacyDeployments(string repoRoot)
    {
        var result = new List<(string, List<string>)>();

        void Inspect(string label, string baseDir)
        {
            var found = new List<string>();
            var skillsBin = Path.Combine(baseDir, "skills", "ck");
            var model     = Path.Combine(baseDir, "models", "bge-small-en-v1.5");
            var hooks     = Path.Combine(baseDir, "hooks");
            var rule      = Path.Combine(baseDir, "rules", "ck-code-search-protocol.md");

            if (Directory.Exists(skillsBin)) found.Add(skillsBin);
            if (Directory.Exists(model))     found.Add(model);
            if (File.Exists(rule))           found.Add(rule);

            // Count ck-owned hook scripts
            if (Directory.Exists(hooks))
            {
                var ckHooks = Directory.GetFiles(hooks)
                    .Where(f => Path.GetFileName(f).StartsWith("ck-") ||
                                Path.GetFileName(f).StartsWith("agent-usage-guard"))
                    .ToList();
                if (ckHooks.Count > 0)
                    found.AddRange(ckHooks);
            }

            if (found.Count > 0)
                result.Add((label, found));
        }

        Inspect("Claude Code (.claude/)",  Path.Combine(repoRoot, ".claude"));
        Inspect("Codex (.codex/)",         Path.Combine(repoRoot, ".codex"));
        Inspect("OpenCode (.opencode/)",   Path.Combine(repoRoot, ".opencode"));
        Inspect("Agents (.agents/)",       Path.Combine(repoRoot, ".agents"));

        return result;
    }

    // ── Legacy migration ───────────────────────────────────────────────────────

    private static async Task MigrateLegacyDeployments(
        string repoRoot,
        List<(string Label, List<string> Paths)> legacyRoots,
        Action<string> print)
    {
        foreach (var (label, _) in legacyRoots)
        {
            // Map label back to the directory prefix
            var baseDir = label switch
            {
                var l when l.StartsWith("Claude Code") => Path.Combine(repoRoot, ".claude"),
                var l when l.StartsWith("Codex")       => Path.Combine(repoRoot, ".codex"),
                var l when l.StartsWith("OpenCode")    => Path.Combine(repoRoot, ".opencode"),
                _                                      => Path.Combine(repoRoot, ".agents"),
            };

            RemoveCkSkills(baseDir, print);
            RemoveCkModels(baseDir, print);
            RemoveCkHooks(baseDir, print);
            RemoveCkRules(baseDir, print);

            if (label.StartsWith("Claude Code"))
            {
                var settings = Path.Combine(baseDir, "settings.json");
                if (File.Exists(settings))
                    await CleanSettingsJson(settings, print);
            }
        }
    }

    private static void RemoveCkSkills(string baseDir, Action<string> print)
    {
        var skillsRoot = Path.Combine(baseDir, "skills");
        if (!Directory.Exists(skillsRoot)) return;

        var ckBin = Path.Combine(skillsRoot, "ck");
        if (Directory.Exists(ckBin))
        {
            Directory.Delete(ckBin, recursive: true);
            print($"  Removed {Path.GetRelativePath(Directory.GetCurrentDirectory(), ckBin)}/");
        }

        foreach (var dir in Directory.GetDirectories(skillsRoot, "ck-*"))
        {
            Directory.Delete(dir, recursive: true);
            print($"  Removed {Path.GetRelativePath(Directory.GetCurrentDirectory(), dir)}/");
        }
    }

    private static void RemoveCkModels(string baseDir, Action<string> print)
    {
        var modelDir = Path.Combine(baseDir, "models", "bge-small-en-v1.5");
        if (!Directory.Exists(modelDir)) return;
        Directory.Delete(modelDir, recursive: true);
        print($"  Removed {Path.GetRelativePath(Directory.GetCurrentDirectory(), modelDir)}/");
    }

    private static void RemoveCkHooks(string baseDir, Action<string> print)
    {
        var hooksDir = Path.Combine(baseDir, "hooks");
        if (!Directory.Exists(hooksDir)) return;

        foreach (var f in Directory.GetFiles(hooksDir))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("ck-") || name.StartsWith("agent-usage-guard"))
            {
                File.Delete(f);
                print($"  Removed {Path.GetRelativePath(Directory.GetCurrentDirectory(), f)}");
            }
        }
    }

    private static void RemoveCkRules(string baseDir, Action<string> print)
    {
        var rule = Path.Combine(baseDir, "rules", "ck-code-search-protocol.md");
        if (!File.Exists(rule)) return;
        File.Delete(rule);
        print($"  Removed {Path.GetRelativePath(Directory.GetCurrentDirectory(), rule)}");
    }

    // Strips CK-specific entries from .claude/settings.json.
    // Only removes entries with project-relative paths (.claude/hooks/..., .claude/skills/...).
    // Globally-registered hooks (absolute paths) are left intact.
    private static async Task CleanSettingsJson(string settingsPath, Action<string> print)
    {
        string raw;
        try { raw = await File.ReadAllTextAsync(settingsPath); }
        catch { return; }

        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch { return; }

        if (root is null) return;

        bool changed = false;

        // Remove CK-owned allowedTools entries (per-repo paths only)
        if (root["permissions"]?["allowedTools"] is JsonArray allowedTools)
        {
            for (int i = allowedTools.Count - 1; i >= 0; i--)
            {
                var entry = allowedTools[i]?.GetValue<string>() ?? "";
                if (entry.Contains(".claude/skills/ck") || entry.Contains(".claude\\skills\\ck"))
                {
                    allowedTools.RemoveAt(i);
                    changed = true;
                }
            }
        }

        // Remove CK-owned hook entries that reference relative .claude/hooks/ paths
        var hookSections = new[] { "PreToolUse", "SubagentStart", "PostToolUse", "SessionStart", "Stop" };
        foreach (var section in hookSections)
        {
            if (root["hooks"]?[section] is not JsonArray groups) continue;

            for (int g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g]?["hooks"] is not JsonArray innerHooks) continue;

                for (int h = innerHooks.Count - 1; h >= 0; h--)
                {
                    var cmd = innerHooks[h]?["command"]?.GetValue<string>() ?? "";
                    if (IsLegacyHookCommand(cmd))
                    {
                        innerHooks.RemoveAt(h);
                        changed = true;
                    }
                }

                if (innerHooks.Count == 0)
                {
                    groups.RemoveAt(g);
                    changed = true;
                }
            }
        }

        if (!changed) return;

        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(settingsPath, root.ToJsonString(opts));
        print($"  Cleaned CK entries from {Path.GetRelativePath(Directory.GetCurrentDirectory(), settingsPath)}");
    }

    private static bool IsLegacyHookCommand(string cmd)
        // Per-repo hook commands always reference project-relative paths.
        // They either start with ".claude/hooks/" or contain it in a bash -c wrapper.
        => cmd.Contains(".claude/hooks/") || cmd.Contains(".claude\\hooks\\");

}
