using ContextKing.Tests.Helpers;
using System.Text.Json;

namespace ContextKing.Tests.Init;

/// <summary>
/// Integration tests for `ck init` behavior verified through its file-system outputs.
/// Invokes the CLI via `dotnet run` so no pre-built binary is required.
/// </summary>
public class InitCommandTests : IDisposable
{
    private readonly TempRepo _repo;

    // Locate the CLI project relative to this test assembly.
    // BaseDirectory: {repo}/src/ContextKing.Tests/bin/Debug/net10.0/
    // 5× ".." → {repo}/  then src/ContextKing.Cli/ContextKing.Cli.csproj
    private static readonly string CliProject = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "ContextKing.Cli", "ContextKing.Cli.csproj"));

    public InitCommandTests() => _repo = new TempRepo();
    public void Dispose() => _repo.Dispose();

    private static async Task<(int exitCode, string stdout, string stderr)> RunCk(
        string args, string workingDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(
            "dotnet", $"run --project \"{CliProject}\" --no-build -- {args}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, stdout, stderr);
    }

    [Fact]
    public async Task Init_CreatesCkJson()
    {
        var (exit, _, _) = await RunCk("init --quiet", _repo.Root);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".ck.json")));
    }

    [Fact]
    public async Task Init_CkJsonContainsMinVersion()
    {
        await RunCk("init --quiet", _repo.Root);
        var json = await File.ReadAllTextAsync(Path.Combine(_repo.Root, ".ck.json"));
        Assert.Contains("minVersion", json);
    }

    [Fact]
    public async Task Init_CkJsonContainsFindFilesDefaults()
    {
        await RunCk("init --quiet", _repo.Root);
        var json = await File.ReadAllTextAsync(Path.Combine(_repo.Root, ".ck.json"));
        using var doc = JsonDocument.Parse(json);
        var findFiles = doc.RootElement.GetProperty("findFiles");

        Assert.True(findFiles.GetProperty("semanticRerank").GetBoolean());
        Assert.Equal(5, findFiles.GetProperty("overfetchMultiplier").GetInt32());
        Assert.Equal(50, findFiles.GetProperty("minOverfetch").GetInt32());
        Assert.Equal(200, findFiles.GetProperty("maxOverfetch").GetInt32());
        Assert.Equal(0.65f, findFiles.GetProperty("lexicalWeight").GetSingle());
        Assert.Equal(0.30f, findFiles.GetProperty("semanticWeight").GetSingle());
        Assert.Equal(0.10f, findFiles.GetProperty("mustWeight").GetSingle());
        Assert.Equal(0.10f, findFiles.GetProperty("genericPenaltyMax").GetSingle());
    }

    [Fact]
    public async Task Init_AddsGitignoreEntry()
    {
        await RunCk("init --quiet", _repo.Root);
        var gitignore = await File.ReadAllTextAsync(Path.Combine(_repo.Root, ".gitignore"));
        Assert.Contains(".ck-index", gitignore);
    }

    [Fact]
    public async Task Init_CreatesKnowledgeDirectory()
    {
        await RunCk("init --quiet", _repo.Root);
        Assert.True(Directory.Exists(Path.Combine(_repo.Root, ".ck-knowledge")));
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".ck-knowledge", ".gitkeep")));
    }

    [Fact]
    public async Task Init_IsIdempotent()
    {
        var (e1, _, _) = await RunCk("init --quiet", _repo.Root);
        var (e2, _, _) = await RunCk("init --quiet", _repo.Root);
        Assert.Equal(0, e1);
        Assert.Equal(0, e2);

        var gitignore = await File.ReadAllTextAsync(Path.Combine(_repo.Root, ".gitignore"));
        Assert.Equal(1, gitignore.Split(".ck-index").Length - 1);
    }

    [Fact]
    public async Task Init_Force_OverwritesCkJson()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repo.Root, ".ck.json"),
            """{ "minVersion": "0.0.1" }""");

        await RunCk("init --force --quiet", _repo.Root);

        var json = await File.ReadAllTextAsync(Path.Combine(_repo.Root, ".ck.json"));
        // After --force, the minVersion should be the current installed version, not "0.0.1".
        Assert.DoesNotContain("0.0.1", json);
    }

    [Fact]
    public async Task Init_FirstRun_BuildsIndex()
    {
        var (exit, _, _) = await RunCk("init --quiet", _repo.Root);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".ck-index", "index.db")));
    }

    [Fact]
    public async Task Init_Force_BuildsIndex()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repo.Root, ".ck.json"),
            """{ "minVersion": "0.0.1" }""");

        var (exit, _, _) = await RunCk("init --force --quiet", _repo.Root);
        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".ck-index", "index.db")));
    }

    [Fact]
    public async Task RequiresInit_BlocksCommandWhenCkJsonMissing()
    {
        var (exit, _, stderr) = await RunCk("find-files --query test", _repo.Root);
        Assert.Equal(1, exit);
        Assert.Contains("not initialized", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ck init", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VersionCheck_BlocksCommandWhenRequiredVersionTooHigh()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_repo.Root, ".ck.json"),
            """{ "minVersion": "999.0.0" }""");

        // `ck find-files` should be blocked by the version requirement.
        var (exit, _, stderr) = await RunCk("find-files --query test", _repo.Root);
        Assert.Equal(2, exit);
        Assert.Contains("999.0.0", stderr);
    }

    [Fact]
    public async Task Init_IsExemptFromVersionCheck()
    {
        // Even if .ck.json demands an impossible version, `ck init` must succeed.
        await File.WriteAllTextAsync(
            Path.Combine(_repo.Root, ".ck.json"),
            """{ "minVersion": "999.0.0" }""");

        var (exit, _, _) = await RunCk("init --force --quiet", _repo.Root);
        Assert.Equal(0, exit);
    }

    // ── Legacy detection ──────────────────────────────────────────────────────

    [Fact]
    public async Task Init_WarnWhenLegacyDeploymentDetected()
    {
        // Plant a fake legacy binary directory (the definitive indicator)
        var legacyBin = Path.Combine(_repo.Root, ".claude", "skills", "ck");
        Directory.CreateDirectory(legacyBin);
        File.WriteAllText(Path.Combine(legacyBin, "ck"), "fake");

        var (exit, stdout, _) = await RunCk("init", _repo.Root);
        Assert.Equal(0, exit);
        // Should warn about legacy deployment
        Assert.Contains("Legacy", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Init_Migrate_RemovesLegacySkillsBinaryDir()
    {
        var legacyBin = Path.Combine(_repo.Root, ".claude", "skills", "ck");
        Directory.CreateDirectory(legacyBin);
        File.WriteAllText(Path.Combine(legacyBin, "ck"), "fake");

        await RunCk("init --migrate", _repo.Root);

        Assert.False(Directory.Exists(legacyBin));
    }

    [Fact]
    public async Task Init_Migrate_RemovesLegacySkillsDirs()
    {
        // Plant multiple ck-* skill dirs as a legacy deployment would
        foreach (var dir in new[] { "ck", "ck-find-scope", "ck-signatures" })
        {
            var p = Path.Combine(_repo.Root, ".claude", "skills", dir);
            Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "SKILL.md"), "---\nname: " + dir + "\n---\ntest");
        }

        await RunCk("init --migrate", _repo.Root);

        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".claude", "skills", "ck")));
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".claude", "skills", "ck-find-scope")));
        Assert.False(Directory.Exists(Path.Combine(_repo.Root, ".claude", "skills", "ck-signatures")));
    }

    [Fact]
    public async Task Init_Migrate_RemovesLegacyModelDir()
    {
        var modelDir = Path.Combine(_repo.Root, ".claude", "models", "bge-small-en-v1.5");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "model.onnx"), "fake");

        await RunCk("init --migrate", _repo.Root);

        Assert.False(Directory.Exists(modelDir));
    }

    [Fact]
    public async Task Init_Migrate_RemovesLegacyHookScripts()
    {
        var hooksDir = Path.Combine(_repo.Root, ".claude", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookFiles = new[] { "ck-bash-guard.sh", "ck-search-guard.sh", "agent-usage-guard.sh" };
        foreach (var f in hookFiles)
            File.WriteAllText(Path.Combine(hooksDir, f), "#!/bin/bash");

        await RunCk("init --migrate", _repo.Root);

        foreach (var f in hookFiles)
            Assert.False(File.Exists(Path.Combine(hooksDir, f)));
    }

    [Fact]
    public async Task Init_Migrate_PreservesNonCkHooks()
    {
        var hooksDir = Path.Combine(_repo.Root, ".claude", "hooks");
        Directory.CreateDirectory(hooksDir);
        File.WriteAllText(Path.Combine(hooksDir, "ck-bash-guard.sh"), "#!/bin/bash");
        File.WriteAllText(Path.Combine(hooksDir, "my-custom-hook.sh"), "#!/bin/bash # mine");

        await RunCk("init --migrate", _repo.Root);

        Assert.False(File.Exists(Path.Combine(hooksDir, "ck-bash-guard.sh")));
        Assert.True(File.Exists(Path.Combine(hooksDir, "my-custom-hook.sh")));
    }

    [Fact]
    public async Task Init_Migrate_CleansSettingsJsonAllowedTools()
    {
        var claudeDir = Path.Combine(_repo.Root, ".claude");
        Directory.CreateDirectory(claudeDir);

        // Write a settings.json that includes legacy CK allowedTools
        var settings = """
            {
              "permissions": {
                "allowedTools": [
                  "Bash(.claude/skills/ck/ck *)",
                  "Bash(.claude\\skills\\ck\\ck.cmd *)",
                  "Bash(npm *)"
                ]
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), settings);

        // Also plant a skills dir so detection triggers
        var ckDir = Path.Combine(claudeDir, "skills", "ck");
        Directory.CreateDirectory(ckDir);
        File.WriteAllText(Path.Combine(ckDir, "ck"), "fake");

        await RunCk("init --migrate", _repo.Root);

        var after = await File.ReadAllTextAsync(Path.Combine(claudeDir, "settings.json"));
        Assert.DoesNotContain(".claude/skills/ck", after);
        Assert.DoesNotContain(".claude\\skills\\ck", after);
        Assert.Contains("npm", after); // non-CK entry preserved
    }

    [Fact]
    public async Task Init_Migrate_CleansSettingsJsonHooks()
    {
        var claudeDir = Path.Combine(_repo.Root, ".claude");
        Directory.CreateDirectory(claudeDir);

        var settings = """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      {"type": "command", "command": ".claude/hooks/ck-bash-guard.sh"},
                      {"type": "command", "command": "my-other-hook.sh"}
                    ]
                  }
                ]
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), settings);

        var ckDir = Path.Combine(claudeDir, "skills", "ck");
        Directory.CreateDirectory(ckDir);
        File.WriteAllText(Path.Combine(ckDir, "ck"), "fake");

        await RunCk("init --migrate", _repo.Root);

        var after = await File.ReadAllTextAsync(Path.Combine(claudeDir, "settings.json"));
        Assert.DoesNotContain(".claude/hooks/ck-bash-guard", after);
        Assert.Contains("my-other-hook.sh", after); // non-CK hook preserved
    }

    [Fact]
    public async Task Init_Migrate_RemovesEmptyMatcherGroupFromSettings()
    {
        var claudeDir = Path.Combine(_repo.Root, ".claude");
        Directory.CreateDirectory(claudeDir);

        // All hooks in this group are CK-owned — the whole group should be removed
        var settings = """
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      {"type": "command", "command": ".claude/hooks/ck-bash-guard.sh"}
                    ]
                  }
                ]
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), settings);

        var ckDir = Path.Combine(claudeDir, "skills", "ck");
        Directory.CreateDirectory(ckDir);
        File.WriteAllText(Path.Combine(ckDir, "ck"), "fake");

        await RunCk("init --migrate", _repo.Root);

        var after = await File.ReadAllTextAsync(Path.Combine(claudeDir, "settings.json"));
        Assert.DoesNotContain("ck-bash-guard", after);
        // The PreToolUse array should now be empty (or the group object should be gone)
        Assert.DoesNotContain("matcher", after);
    }

    [Fact]
    public async Task Init_Migrate_DetectsMultipleClis()
    {
        // Plant legacy assets for both Claude Code and Codex
        foreach (var cli in new[] { ".claude", ".codex" })
        {
            var skillsDir = Path.Combine(_repo.Root, cli, "skills", "ck");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "ck"), "fake");
        }

        var (_, stdout, _) = await RunCk("init", _repo.Root);
        Assert.Contains("Claude Code", stdout);
        Assert.Contains("Codex", stdout);
    }
}
