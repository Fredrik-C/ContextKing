using ContextKing.Core.Knowledge;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Knowledge;

public sealed class KnowledgeStoreTests : IDisposable
{
    private readonly TempRepo _repo = new();

    // ── ReadAll ───────────────────────────────────────────────────────────────

    [Fact]
    public void ReadAll_WhenNoFile_ReturnsEmpty()
    {
        new KnowledgeStore(_repo.Root).ReadAll().Should().BeEmpty();
    }

    [Fact]
    public void ReadAll_ReturnsAllAppendedSnippets()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "Content A", ["tag1"], ["src/Payments/"]));
        store.Append(Snippet("b2", "Content B", ["tag2"], ["src/Users/"]));

        store.ReadAll().Should().HaveCount(2);
    }

    [Fact]
    public void ReadAll_AggregatesLegacyAndSessionJsonlFiles()
    {
        var legacyPath = KnowledgeStore.SnippetsPath(_repo.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonLine(Snippet("legacy", "Legacy content")));

        var sessionPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "session-a");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        File.WriteAllText(sessionPath, JsonLine(Snippet("session", "Session content")));

        var all = new KnowledgeStore(_repo.Root).ReadAll();

        all.Select(s => s.Id).Should().BeEquivalentTo(["legacy", "session"]);
    }

    [Fact]
    public void ReadAll_SkipsMalformedLines()
    {
        var path = KnowledgeStore.SnippetsPath(_repo.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            """
            {"id":"ok1","content":"Good","created_at":"2026-01-01T00:00:00Z"}
            NOT VALID JSON
            {"id":"ok2","content":"Also good","created_at":"2026-01-02T00:00:00Z"}
            """);

        var store = new KnowledgeStore(_repo.Root);
        store.ReadAll().Should().HaveCount(2);
    }

    // ── Append ────────────────────────────────────────────────────────────────

    [Fact]
    public void Append_CreatesDirectoryIfAbsent()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("x1", "Hello"));

        File.Exists(store.FilePath).Should().BeTrue();
    }

    [Fact]
    public void Append_WritesToSessionSpecificFileFromEnvironment()
    {
        using var env = new EnvironmentVariableScope("CK_SESSION_ID", "test/session:one");
        var store = new KnowledgeStore(_repo.Root);

        store.Append(Snippet("x1", "Hello"));

        var expectedPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "test-session-one");
        File.Exists(expectedPath).Should().BeTrue();
        File.Exists(KnowledgeStore.SnippetsPath(_repo.Root)).Should().BeFalse();
    }

    [Fact]
    public void Append_WithoutSessionEnv_SharesOneBranchScopedFileAcrossInstances()
    {
        // Each `ck learn` is a separate process → a fresh KnowledgeStore. Without a
        // CK_SESSION_ID, every entry on the same branch must land in one file rather
        // than spawning a new session file per invocation.
        using var env = new EnvironmentVariableScope("CK_SESSION_ID", null);

        var processA = new KnowledgeStore(_repo.Root);
        var processB = new KnowledgeStore(_repo.Root);
        processA.Append(Snippet("a1", "From invocation A"));
        processB.Append(Snippet("b2", "From invocation B"));

        processA.FilePath.Should().Be(processB.FilePath);

        var branch = ContextKing.Core.Git.GitTracker.GetCurrentBranch(_repo.Root);
        var expected = KnowledgeStore.SessionSnippetsPath(_repo.Root, $"branch-{branch}");
        processA.FilePath.Should().Be(expected);

        var store = new KnowledgeStore(_repo.Root);
        store.KnowledgeFiles().Should().ContainSingle();
        store.ReadAll().Select(s => s.Id).Should().BeEquivalentTo(["a1", "b2"]);
    }

    [Fact]
    public void Append_PreservesExistingSnippets()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "First"));
        store.Append(Snippet("b2", "Second"));

        var all = store.ReadAll();
        all.Select(s => s.Id).Should().BeEquivalentTo(["a1", "b2"]);
    }

    // ── ReadByFolder ──────────────────────────────────────────────────────────

    [Fact]
    public void ReadByFolder_ExactMatch_ReturnSnippets()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "Payment logic", folders: ["src/Payments/Adyen/"]));
        store.Append(Snippet("b2", "User logic",    folders: ["src/Users/"]));

        var results = store.ReadByFolder("src/Payments/Adyen/");
        results.Should().HaveCount(1);
        results[0].Id.Should().Be("a1");
    }

    [Fact]
    public void ReadByFolder_PrefixMatch_ReturnsParentSnippets()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "All payments", folders: ["src/Payments/"]));

        // Query for a child folder — parent snippet should match
        var results = store.ReadByFolder("src/Payments/Adyen/Terminal/");
        results.Should().HaveCount(1);
    }

    [Fact]
    public void ReadByFolder_SortedNewestFirst()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("old", "Older",  folders: ["src/Payments/"],
            createdAt: "2026-01-01T00:00:00Z"));
        store.Append(Snippet("new", "Newer",  folders: ["src/Payments/"],
            createdAt: "2026-04-22T00:00:00Z"));

        var results = store.ReadByFolder("src/Payments/");
        results[0].Id.Should().Be("new");
        results[1].Id.Should().Be("old");
    }

    [Fact]
    public void ReadByFolder_WhenNoMatch_ReturnsEmpty()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "Users", folders: ["src/Users/"]));

        store.ReadByFolder("src/Payments/").Should().BeEmpty();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesSnippetById()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("keep", "Keep me"));
        store.Append(Snippet("gone", "Remove me"));

        store.Delete("gone").Should().BeTrue();

        var all = store.ReadAll();
        all.Should().HaveCount(1);
        all[0].Id.Should().Be("keep");
    }

    [Fact]
    public void Delete_RemovesSnippetFromContainingJsonlOnly()
    {
        var legacyPath = KnowledgeStore.SnippetsPath(_repo.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonLine(Snippet("legacy", "Legacy content")));

        var sessionPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "session-a");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        File.WriteAllText(sessionPath,
            JsonLine(Snippet("gone", "Remove me")) +
            JsonLine(Snippet("keep-session", "Keep me")));

        new KnowledgeStore(_repo.Root).Delete("gone").Should().BeTrue();

        File.ReadAllText(legacyPath).Should().Contain("legacy");
        var sessionText = File.ReadAllText(sessionPath);
        sessionText.Should().NotContain("gone");
        sessionText.Should().Contain("keep-session");
    }

    [Fact]
    public void ReplaceAll_PreservesExistingSnippetFiles()
    {
        var legacyPath = KnowledgeStore.SnippetsPath(_repo.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, JsonLine(Snippet("legacy", "Legacy content")));

        var sessionPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "session-a");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        File.WriteAllText(sessionPath, JsonLine(Snippet("session", "Session content")));

        var store = new KnowledgeStore(_repo.Root);
        store.ReplaceAll([
            Snippet("legacy", "Legacy updated"),
            Snippet("session", "Session updated")
        ]);

        File.ReadAllText(legacyPath).Should().Contain("Legacy updated");
        File.ReadAllText(sessionPath).Should().Contain("Session updated");
    }

    [Fact]
    public void Delete_ReturnsFalse_WhenIdNotFound()
    {
        var store = new KnowledgeStore(_repo.Root);
        store.Append(Snippet("a1", "Something"));

        store.Delete("no-such-id").Should().BeFalse();
        store.ReadAll().Should().HaveCount(1);
    }

    [Fact]
    public void Delete_WhenNoFile_ReturnsFalse()
    {
        new KnowledgeStore(_repo.Root).Delete("any-id").Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static KnowledgeSnippet Snippet(
        string id,
        string content,
        IReadOnlyList<string>? tags    = null,
        IReadOnlyList<string>? folders = null,
        string? createdAt = null) =>
        new()
        {
            Id        = id,
            Content   = content,
            Tags      = tags    ?? [],
            Folders   = folders ?? [],
            CreatedAt = createdAt ?? "2026-04-22T10:00:00Z",
        };

    private static string JsonLine(KnowledgeSnippet snippet) =>
        System.Text.Json.JsonSerializer.Serialize(snippet) + "\n";

    public void Dispose() => _repo.Dispose();

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
