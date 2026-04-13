using ContextKing.Core.Git;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Git;

public class GitTrackerTests : IDisposable
{
    private readonly TempRepo _repo = new();

    // ── GetWorktreeRoot ────────────────────────────────────────────────────────

    [Fact]
    public void GetWorktreeRoot_FromRepoRoot_ReturnsPathContainingDotGit()
    {
        // git resolves symlinks (e.g. /var → /private/var on macOS), so we cannot
        // do a direct string comparison against _repo.Root. Instead verify the
        // returned path is a valid git root.
        var root = GitTracker.GetWorktreeRoot(_repo.Root);
        Directory.Exists(Path.Combine(root, ".git")).Should().BeTrue();
    }

    [Fact]
    public void GetWorktreeRoot_FromSubdirectory_ReturnsSamePathAsFromRoot()
    {
        var sub = Path.Combine(_repo.Root, "src", "Payment");
        Directory.CreateDirectory(sub);

        // Both paths resolve through git, so symlink differences cancel out.
        var fromRoot = GitTracker.GetWorktreeRoot(_repo.Root);
        var fromSub  = GitTracker.GetWorktreeRoot(sub);

        fromSub.Should().Be(fromRoot);
    }

    // ── GetHead ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetHead_NoCommits_ReturnsUnknown()
    {
        // Fresh repo with no commits — git rev-parse --short HEAD fails
        var head = GitTracker.GetHead(_repo.Root);
        head.Should().Be("unknown");
    }

    [Fact]
    public void GetHead_AfterCommit_ReturnsAbbreviatedHash()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();

        var head = GitTracker.GetHead(_repo.Root);

        head.Should().MatchRegex("^[0-9a-f]{5,}$",
            "abbreviated hash is 5+ hex characters");
    }

    // ── ListCsFilesByFolder ────────────────────────────────────────────────────

    [Fact]
    public void ListCsFilesByFolder_EmptyRepo_ReturnsEmpty()
    {
        var result = GitTracker.ListCsFilesByFolder(_repo.Root);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ListCsFilesByFolder_GroupsByFolder()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Payment/StripeGateway.cs");
        _repo.WriteFile("src/Auth/AuthController.cs");
        _repo.StageAndCommit();

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result.Should().ContainKey("src/Payment");
        result.Should().ContainKey("src/Auth");
        result["src/Payment"].Keys.Should().BeEquivalentTo(
            ["PaymentService.cs", "StripeGateway.cs"]);
        result["src/Auth"].Keys.Should().BeEquivalentTo(["AuthController.cs"]);
    }

    [Fact]
    public void ListCsFilesByFolder_ExcludesTestFoldersByDefault()
    {
        // The exclusion logic does exact segment matching: a segment must equal "Tests"
        // (case-insensitive). Use bare segment names, not dotted names like "Payment.Tests".
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Tests/PaymentServiceTests.cs");
        _repo.WriteFile("src/Specs/SpecFile.cs");
        _repo.StageAndCommit();

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result.Should().ContainKey("src/Payment");
        result.Keys.Should().NotContain("src/Tests");
        result.Keys.Should().NotContain("src/Specs");
    }

    [Fact]
    public void ListCsFilesByFolder_IgnoresNonCsFiles()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Payment/README.md");
        _repo.WriteFile("src/Payment/config.json");
        _repo.StageAndCommit();

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result["src/Payment"].Keys.Should().BeEquivalentTo(["PaymentService.cs"]);
    }

    [Fact]
    public void ListCsFilesByFolder_UntrackedFile_IsIncluded()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();

        // Write a new file but do NOT stage or commit it
        _repo.WriteFile("src/Payment/NewService.cs");

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result["src/Payment"].Keys.Should().Contain("NewService.cs");
    }

    [Fact]
    public void ListCsFilesByFolder_TrackedFiledDeletedFromDisk_IsExcluded()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.WriteFile("src/Payment/OldService.cs");
        _repo.StageAndCommit();

        // Delete from disk without staging the deletion
        _repo.DeleteFile("src/Payment/OldService.cs");

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result["src/Payment"].Keys.Should().NotContain("OldService.cs");
        result["src/Payment"].Keys.Should().Contain("PaymentService.cs");
    }

    [Fact]
    public void ListCsFilesByFolder_FileHashes_AreNonEmpty()
    {
        _repo.WriteFile("src/Payment/PaymentService.cs");
        _repo.StageAndCommit();

        var result = GitTracker.ListCsFilesByFolder(_repo.Root);

        result["src/Payment"]["PaymentService.cs"].Should().NotBeNullOrEmpty();
    }

    // ── GetBranch ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetBranch_AfterFirstCommit_ReturnsNonEmptyString()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();

        var branch = GitTracker.GetBranch(_repo.Root);

        branch.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GetBranch_AfterCheckout_ReturnsNewBranchName()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();
        _repo.Git("checkout -b feature");

        var branch = GitTracker.GetBranch(_repo.Root);

        branch.Should().Be("feature");
    }

    // ── ComputeStateKey ────────────────────────────────────────────────────────

    [Fact]
    public void ComputeStateKey_SameFiles_ReturnsSameKey()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();

        var k1 = GitTracker.ComputeStateKey(_repo.Root);
        var k2 = GitTracker.ComputeStateKey(_repo.Root);

        k1.Should().Be(k2);
    }

    [Fact]
    public void ComputeStateKey_AfterFileAdded_KeyChanges()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();
        var before = GitTracker.ComputeStateKey(_repo.Root);

        _repo.WriteFile("src/B.cs");
        _repo.StageAndCommit("add B");
        var after = GitTracker.ComputeStateKey(_repo.Root);

        after.Should().NotBe(before);
    }

    [Fact]
    public void ComputeStateKey_ContentOnlyChange_KeyChanges()
    {
        // Write file, commit, record key
        _repo.WriteFile("src/A.cs", "// original");
        _repo.StageAndCommit();
        var before = GitTracker.ComputeStateKey(_repo.Root);

        // Change the content but keep the filename
        _repo.WriteFile("src/A.cs", "// changed content");
        _repo.StageAndCommit("content change");
        var after = GitTracker.ComputeStateKey(_repo.Root);

        // Content hashes changed — key must differ
        after.Should().NotBe(before);
    }

    [Fact]
    public void ComputeStateKey_AfterBranchSwitch_SameFiles_KeyChanges()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();
        var before = GitTracker.ComputeStateKey(_repo.Root);

        _repo.Git("checkout -b feature");
        var after = GitTracker.ComputeStateKey(_repo.Root);

        after.Should().NotBe(before, "switching branches must invalidate the index");
    }

    [Fact]
    public void ComputeStateKey_ReturnsShortHexString()
    {
        _repo.WriteFile("src/A.cs");
        _repo.StageAndCommit();

        var key = GitTracker.ComputeStateKey(_repo.Root);

        key.Should().MatchRegex("^[0-9A-F]{16}$",
            "state key is 16 uppercase hex characters");
    }

    // ── IsExcluded (internal helper) ──────────────────────────────────────────

    [Fact]
    public void IsExcluded_TestsSegment_ReturnsTrue()
        => GitTracker.IsExcluded("src/Tests/Foo.cs", ["Tests"])
            .Should().BeTrue();

    [Fact]
    public void IsExcluded_NonMatchingSegments_ReturnsFalse()
        => GitTracker.IsExcluded("src/Payment/PaymentService.cs", ["Tests"])
            .Should().BeFalse();

    [Fact]
    public void IsExcluded_CaseInsensitive()
        => GitTracker.IsExcluded("src/TESTS/Foo.cs", ["Tests"])
            .Should().BeTrue();

    [Fact]
    public void IsExcluded_EmptyExclusions_AlwaysReturnsFalse()
        => GitTracker.IsExcluded("src/Tests/Foo.cs", [])
            .Should().BeFalse();

    public void Dispose() => _repo.Dispose();
}
