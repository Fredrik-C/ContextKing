using ContextKing.Core.SourceMap;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.SourceMap;

public class SourceMapBuilderTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public void GetStatus_NoIndex_ReturnsMissing()
    {
        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Missing);
    }

    [Fact]
    public async Task BuildAsync_CreatesIndexFile()
    {
        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "ProcessPayment");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        File.Exists(SourceMapBuilder.GetDbPath(_repo.Root)).Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_LoadsIndexedFiles()
    {
        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "ProcessPayment");
        WriteIndexedClass("src/Users/UserService.cs", "UserService", "LoadUser");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var files = LoadIndexedFiles();
        files.Select(f => f.Path).Should().BeEquivalentTo([
            "src/Payment/PaymentService.cs",
            "src/Users/UserService.cs"
        ]);
    }

    [Fact]
    public async Task BuildAsync_ExtractsTypeAndMethodNames()
    {
        WriteIndexedClass("src/Payment/StripeGateway.cs", "StripeGateway", "ProcessRefund");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var file = LoadIndexedFiles().Single(f => f.Path == "src/Payment/StripeGateway.cs");
        file.TypeNames.Should().Contain("StripeGateway");
        file.MethodNames.Should().Contain("ProcessRefund");
        file.TypeCount.Should().BeGreaterThan(0);
        file.SignatureCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task BuildAsync_ExcludesTestFolders()
    {
        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "ProcessPayment");
        WriteIndexedClass("src/Tests/PaymentServiceTests.cs", "PaymentServiceTests", "ShouldProcess");
        _repo.StageAndCommit();

        await Builder().BuildAsync(_repo.Root);

        var paths = LoadIndexedFiles().Select(f => f.Path).ToArray();
        paths.Should().Contain("src/Payment/PaymentService.cs");
        paths.Should().NotContain("src/Tests/PaymentServiceTests.cs");
    }

    [Fact]
    public async Task GetStatus_AfterBuild_ReturnsFresh_AndAfterContentChange_ReturnsStale()
    {
        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "ProcessPayment");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Fresh);

        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "RefundPayment");
        _repo.StageAndCommit("modify");

        SourceMapBuilder.GetStatus(_repo.Root).Should().Be(IndexStatus.Stale);
    }

    [Fact]
    public async Task BuildAsync_Incremental_ReportsUpdatedFileCount()
    {
        WriteIndexedClass("src/Payment/PaymentService.cs", "PaymentService", "ProcessPayment");
        WriteIndexedClass("src/Users/UserService.cs", "UserService", "LoadUser");
        _repo.StageAndCommit();
        await Builder().BuildAsync(_repo.Root);

        WriteIndexedClass("src/Users/UserService.cs", "UserService", "LoadAndSaveUser");
        _repo.StageAndCommit("change users");

        var progress = new List<string>();
        await Builder().BuildAsync(_repo.Root, progress: new Progress<string>(progress.Add));

        progress.Should().Contain(m => m.Contains("Index complete:") && m.Contains("files updated"));
    }

    private SourceMapBuilder Builder() => new();

    private IReadOnlyList<IndexedFile> LoadIndexedFiles()
    {
        var dbPath = SourceMapBuilder.GetDbPath(_repo.Root);
        return new SourceMapIndex(dbPath).LoadIndexedFiles();
    }

    private void WriteIndexedClass(string relativePath, string typeName, string methodName)
    {
        _repo.WriteFile(relativePath, $$"""
            namespace Demo;
            public class {{typeName}}
            {
                public void {{methodName}}() { }
            }
            """);
    }

    public void Dispose() => _repo.Dispose();
}
