using ContextKing.Core.Embedding;
using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

/// <summary>
/// Integration tests for SourceMapBuilder. Each test gets its own TempRepo so
/// git state is fully isolated. A single BgeEmbedder is shared across the class
/// to avoid repeated model loads.
/// </summary>
public class SourceMapBuilderTests : IClassFixture<EmbedderFixture>, IDisposable
{
    private readonly BgeEmbedder _embedder;
    private readonly TempRepo    _repo = new();

    public SourceMapBuilderTests(EmbedderFixture fixture)
        => _embedder = fixture.Embedder;

    // ── Status — no index ─────────────────────────────────────────────────────

    [Fact]
    public void GetStatus_NoIndex_ReturnsMissing()
    {
        // The TempRepo root has no .ck-index directory yet.
        var status = SourceMapBuilder.GetStatus(_repo.Root);
        status.Should().Be(IndexStatus.Missing);
    }

    // ── Basic build ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_CreatesIndexFile()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
        File.Exists(dbPath).Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_IndexContainsCorrectFolders()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Users/UserService.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var folders = LoadIndexed();
        folders.Select(f => f.Path).Should().BeEquivalentTo(["src/Payment", "src/Users"]);
    }

    [Fact]
    public async Task BuildAsync_CombinedTokensContainPathAndFileTokens()
    {
        _repo.WriteFile("src/Payment/StripeGateway.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var folder = LoadIndexed().Single(f => f.Path == "src/Payment");
        folder.CombinedTokens.Should().Contain("payment");
        folder.CombinedTokens.Should().Contain("stripe");
        folder.CombinedTokens.Should().Contain("gateway");
    }

    [Fact]
    public async Task BuildAsync_CombinedTokens_AreDistinct_NoDuplicates()
    {
        // "Payment" appears in both the folder path and the filename — should appear once.
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var folder  = LoadIndexed().Single(f => f.Path == "src/Payment");
        var tokens  = folder.CombinedTokens.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var distinct = tokens.Distinct(StringComparer.Ordinal).ToArray();
        tokens.Should().BeEquivalentTo(distinct,
            "combined_tokens must not contain duplicate entries");
    }

    [Fact]
    public async Task BuildAsync_CombinedTokensContainPublicMethodNames()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs", """
            namespace Payment;
            public class PaymentService
            {
                public void ProcessPayment() { }
                public string CalculateTotal() => "";
                private void InternalHelper() { }
            }
            """);
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var folder = LoadIndexed().Single(f => f.Path == "src/Payment");
        // Method names are camelCase-split in combined tokens for exact matching
        folder.CombinedTokens.Should().Contain("process");
        folder.CombinedTokens.Should().Contain("calculate");
        folder.CombinedTokens.Should().Contain("total");
        folder.CombinedTokens.Should().NotContain("InternalHelper",
            "private methods should not be included");
        folder.CombinedTokens.Should().NotContain("internal");

        // Embedding text should contain readable method phrases
        folder.EmbeddingText.Should().Contain("Process Payment");
        folder.EmbeddingText.Should().Contain("Calculate Total");
    }

    [Fact]
    public async Task BuildAsync_MethodNamesSplitForMatchButReadableForEmbedding()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs", """
            namespace Payment;
            public class PaymentService
            {
                public void ProcessPayment() { }
            }
            """);
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var folder = LoadIndexed().Single(f => f.Path == "src/Payment");
        // Combined tokens: camelCase-split lowercase for exact matching
        var tokens = folder.CombinedTokens.Split(' ');
        tokens.Should().Contain("process");
        tokens.Should().Contain("payment");
        tokens.Should().NotContain("ProcessPayment",
            "method names should be camelCase-split in match tokens");

        // Embedding text: readable phrase preserving word boundaries
        folder.EmbeddingText.Should().Contain("Process Payment");
    }

    [Fact]
    public async Task GetStatus_AfterContentChange_ReturnsStale()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs", "// v1");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);
        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Fresh);

        // Modify file content without adding/removing files
        _repo.WriteFile("src/Payment/PaymentService.cs", "// v2 with changes");
        _repo.StageAndCommit("modify payment service");

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Stale,
            "content changes should now trigger staleness");
    }

    [Fact]
    public async Task BuildAsync_Incremental_ContentChangeTriggersReembed()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs", """
            namespace Payment;
            public class PaymentService
            {
                public void ProcessPayment() { }
            }
            """);
        _repo.WriteFile("src/Users/UserService.cs", "// placeholder");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        // Modify content in Payment only — adds a new public method
        _repo.WriteFile("src/Payment/PaymentService.cs", """
            namespace Payment;
            public class PaymentService
            {
                public void ProcessPayment() { }
                public void RefundPayment() { }
            }
            """);
        _repo.StageAndCommit("add refund method");

        var messages = new List<string>();
        await Builder().BuildAsync(_repo.Root, progress: new Progress<string>(messages.Add));

        // Payment should be re-embedded, Users unchanged
        var summary = messages.LastOrDefault(m => m.Contains("updated") && m.Contains("unchanged"));
        summary.Should().NotBeNull();
        summary.Should().Contain("1 updated");
        summary.Should().Contain("1 unchanged");

        // Verify the new method name appears in tokens (camelCase-split)
        var folder = LoadIndexed().Single(f => f.Path == "src/Payment");
        folder.CombinedTokens.Should().Contain("refund");
        folder.EmbeddingText.Should().Contain("Refund Payment");
    }

    // ── Exclusions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_ExcludesTestFolders()
    {
        // Exclusion logic matches exact path segments, not dotted names.
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Tests/PaymentServiceTests.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var paths = LoadIndexed().Select(f => f.Path);
        paths.Should().Contain("src/Payment");
        paths.Should().NotContain("src/Tests", "test folders are excluded by default");
    }

    // ── Status after build ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_AfterBuild_ReturnsFresh()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Fresh);
    }

    [Fact]
    public async Task GetStatus_AfterAddingNewFile_ReturnsStale()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        // Add a new file and commit — this changes the filename-set fingerprint
        _repo.WriteFile("src/Payment/StripeGateway.cs");
        _repo.StageAndCommit("add stripe gateway");

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Stale);
    }

    [Fact]
    public async Task GetStatus_AfterBranchSwitch_SameFiles_ReturnsStale()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);
        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Fresh);

        _repo.Git("checkout -b feature");

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Stale,
            "switching branches must invalidate the index even when filenames are unchanged");
    }

    // ── Incremental builds ────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_Incremental_ReportsUnchangedFolders()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Users/UserService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        // Add a file only to Users — Payment should be skipped on second build
        _repo.WriteFile("src/Users/UserRepository.cs");
        _repo.StageAndCommit("add user repository");

        var messages = new List<string>();
        await Builder().BuildAsync(_repo.Root, progress: new Progress<string>(messages.Add));

        // Exactly one folder re-embedded (Users), one skipped (Payment)
        var summary = messages.LastOrDefault(m => m.Contains("updated") && m.Contains("unchanged"));
        summary.Should().NotBeNull("builder should emit a final summary message");
        summary.Should().Contain("1 updated");
        summary.Should().Contain("1 unchanged");
    }

    [Fact]
    public async Task BuildAsync_Incremental_UnchangedFolderEmbeddingPreserved()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Users/UserService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        var firstEmbedding = LoadIndexed()
            .Single(f => f.Path == "src/Payment")
            .Embedding;

        // Touch only Users
        _repo.WriteFile("src/Users/UserRepository.cs");
        _repo.StageAndCommit("add user repository");
        await Builder().BuildAsync(_repo.Root);

        var secondEmbedding = LoadIndexed()
            .Single(f => f.Path == "src/Payment")
            .Embedding;

        secondEmbedding.Should().Equal(firstEmbedding);
    }

    // ── Force rebuild ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_ForceRebuild_ReembeddsAllFolders()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        var messages = new List<string>();
        await Builder().BuildAsync(_repo.Root, forceRebuild: true,
            progress: new Progress<string>(messages.Add));

        var summary = messages.LastOrDefault(m => m.Contains("updated") && m.Contains("unchanged"));
        summary.Should().NotBeNull();
        summary.Should().Contain("1 updated");
        summary.Should().Contain("0 unchanged");
    }

    // ── Folder deletion ───────────────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_AfterFolderDeleted_FolderRemovedFromIndex()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Users/UserService.cs");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        // Remove the Users folder from git and rebuild
        _repo.Git("rm src/Users/UserService.cs");
        _repo.StageAndCommit("remove users");
        await Builder().BuildAsync(_repo.Root);

        var paths = LoadIndexed().Select(f => f.Path);
        paths.Should().Contain("src/Payment");
        paths.Should().NotContain("src/Users");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private SourceMapBuilder Builder() => new(_embedder);

    private IReadOnlyList<IndexedFolder> LoadIndexed()
    {
        var dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
        return new SourceMapIndex(dbPath).LoadIndexedFolders();
    }

    public void Dispose() => _repo.Dispose();
}
