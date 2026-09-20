using ContextKing.Cli.Commands;
using ContextKing.Core.Embedding;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class FindFilesCommandTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public async Task RunAsync_WithTaskStillWorks()
    {
        WriteClass("src/Payments/AdyenTerminalRefundService.cs", "AdyenTerminalRefundService", "RetryTerminalRefund");
        _repo.StageAndCommit();

        var result = await RunCommand(
            "adyen terminal refund retry",
            "--task",
            "Find retry handling for terminal refunds.",
            "--repo",
            _repo.Root);

        result.ExitCode.Should().Be(0, $"stdout: {result.Stdout}; stderr: {result.Stderr}");
        result.Stdout.Should().Contain("src/Payments/AdyenTerminalRefundService.cs");
    }

    [Fact]
    public async Task RunAsync_DefaultTopLimitsFirstDiscoveryResponseToFiveFiles()
    {
        for (var i = 1; i <= 6; i++)
            WriteClass($"src/Payments/RefundService{i}.cs", $"RefundService{i}", "ProcessRefund");
        _repo.WriteFile(".ck.json", """{ "findFiles": { "semanticRerank": false, "methodRerank": false } }""");
        _repo.StageAndCommit();

        var result = await RunCommand(
            "refund service process",
            "--task",
            "Find refund service processing implementation.",
            "--repo",
            _repo.Root);

        result.ExitCode.Should().Be(0, $"stdout: {result.Stdout}; stderr: {result.Stderr}");
        result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(5);
    }

    [Fact]
    public async Task RunAsync_MissingTaskFails()
    {
        WriteClass("src/Payments/AdyenTerminalRefundService.cs", "AdyenTerminalRefundService", "RetryTerminalRefund");
        _repo.StageAndCommit();

        var result = await RunCommand("adyen terminal refund retry", "--repo", _repo.Root);

        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("--task is required");
    }

    [Fact]
    public async Task RunAsync_TaskIsAcceptedAndFallbackReturnsLexicalResultsWhenRerankerUnavailable()
    {
        WriteClass("src/Payments/AdyenTerminalRefundService.cs", "AdyenTerminalRefundService", "RetryTerminalRefund");
        _repo.WriteFile(".ck.json", """{ "findFiles": { "semanticRerank": true, "overfetchMultiplier": 5 } }""");
        _repo.StageAndCommit();
        using var emptyModelDir = new TempDirectory();
        var oldModelDir = Environment.GetEnvironmentVariable("CK_MODEL_DIR");
        Environment.SetEnvironmentVariable("CK_MODEL_DIR", emptyModelDir.Path);
        try
        {
            var result = await RunCommand(
                "adyen terminal refund retry",
                "--task",
                "Find retry handling for terminal refunds after transient provider errors.",
                "--explain",
                "--repo",
                _repo.Root);

            result.ExitCode.Should().Be(0, $"stdout: {result.Stdout}; stderr: {result.Stderr}");
            result.Stdout.Should().Contain("src/Payments/AdyenTerminalRefundService.cs");
            result.Stdout.Should().Contain("semantic=unavailable");
            result.Stderr.Should().Contain("semantic rerank unavailable");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CK_MODEL_DIR", oldModelDir);
        }
    }

    [Fact]
    public async Task RunAsync_ExplainShowsSemanticFields()
    {
        WriteClass("src/Payments/RefundService.cs", "RefundService", "ProcessRefund");
        _repo.StageAndCommit();

        var result = await RunCommand(
            "refund process",
            "--task",
            "Find refund processing implementation.",
            "--explain",
            "--repo",
            _repo.Root);

        result.ExitCode.Should().Be(0, $"stdout: {result.Stdout}; stderr: {result.Stderr}");
        result.Stdout.Should().Contain("lexical=");
        result.Stdout.Should().Contain("semantic=");
        result.Stdout.Should().Contain("matched=");
    }

    [Fact]
    public void Help_DoesNotExposeSemanticTuningFlags()
    {
        var result = Capture(() => FindFilesCommand.RunAsync(["--help"]).GetAwaiter().GetResult());

        result.Stdout.Should().Contain("--task <text>");
        result.Stdout.Should().Contain("--task is required");
        result.Stdout.Should().NotContain("--semantic");
        result.Stdout.Should().NotContain("--overfetch");
        result.Stdout.Should().NotContain("--semantic-weight");
        result.Stdout.Should().NotContain("--lexical-weight");
    }

    [Fact]
    public async Task RunAsync_SemanticDisabledKeepsLexicalOrdering()
    {
        WriteClass("src/A/RefundPaymentService.cs", "RefundPaymentService", "RefundPayment");
        WriteClass("src/B/PaymentWorkflow.cs", "PaymentWorkflow", "Payment");
        _repo.WriteFile(".ck.json", """{ "findFiles": { "semanticRerank": false, "methodRerank": false } }""");
        _repo.StageAndCommit();

        var result = await RunCommand(
            "refund payment",
            "--task",
            "Find refund payment workflow files.",
            "--top",
            "2",
            "--repo",
            _repo.Root);

        result.ExitCode.Should().Be(0, $"stdout: {result.Stdout}; stderr: {result.Stderr}");
        result.Stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]
            .Should().Contain("src/A/RefundPaymentService.cs");
    }

    [Fact]
    public async Task MethodStageRanksBehaviorAndExplainsWithoutSource()
    {
        _repo.WriteFile("src/Target/Refund.cs", "class Refund { void Execute() { try { Send(); } catch (TimeoutException) { Retry(); } Log(\"secret-value\"); } }");
        _repo.WriteFile("src/Card/Refund.cs", "class Refund { void Execute() { Send(); } }");
        _repo.WriteFile(".ck.json", """{"findFiles":{"semanticRerank":false,"methodRerank":true}}""");
        _repo.StageAndCommit();
        var embedder = new BehaviorEmbedder();
        var result = await CaptureAsync(() => FindFilesCommand.RunAsync(
            ["refund", "--task", "Find terminal refund handling that retries after transient provider errors.", "--explain", "--repo", _repo.Root], _ => embedder));
        result.ExitCode.Should().Be(0);
        result.Stdout.Split(Environment.NewLine)[0].Should().Contain("src/Target/Refund.cs");
        result.Stdout.Should().Contain("best_member=Execute").And.Contain("method=1.0000").And.NotContain("secret-value");
        result.Stderr.Should().BeEmpty();
        embedder.Query.Should().Be("Find terminal refund handling that retries after transient provider errors.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NegativeIntentIsUnchangedAndWarnsOnlyOnceWhenVerbose(bool verbose)
    {
        _repo.WriteFile("src/Refund.cs", "class Refund { void Execute() { Retry(); } }");
        _repo.WriteFile(".ck.json", """{"findFiles":{"semanticRerank":false,"methodRerank":true}}""");
        _repo.StageAndCommit();
        var task = "Find refund retry. Ignore cards. Exclude tests without recovery.";
        var args = new List<string> { "refund", "--task", task, "--repo", _repo.Root };
        if (verbose) args.Add("--verbose");
        var embedder = new BehaviorEmbedder();
        var result = await CaptureAsync(() => FindFilesCommand.RunAsync(args.ToArray(), _ => embedder));
        result.ExitCode.Should().Be(0);
        embedder.Query.Should().Be(task);
        result.Stderr.Split("appears to contain an exclusion").Length.Should().Be(verbose ? 2 : 1);
        result.Stdout.Trim().Split('\t').Should().HaveCount(2);
    }

    [Fact]
    public async Task MissingOptionalModelSucceedsAndDoesNotLogSourceException()
    {
        _repo.WriteFile("src/Refund.cs", "class Refund { void Execute() { Retry(); } }");
        _repo.WriteFile(".ck.json", """{"findFiles":{"semanticRerank":false,"methodRerank":true}}""");
        _repo.StageAndCommit();
        var result = await CaptureAsync(() => FindFilesCommand.RunAsync(
            ["refund", "--task", "Find refund retries", "--explain", "--repo", _repo.Root],
            _ => throw new FileNotFoundException("secret-source-input")));
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("method=unavailable");
        result.Stderr.Should().Contain("method rerank unavailable").And.NotContain("secret-source-input");
    }

    [Fact]
    public async Task DisabledMethodStageDoesNotLoadModel()
    {
        WriteClass("src/Refund.cs", "Refund", "Retry");
        _repo.WriteFile(".ck.json", """{"findFiles":{"semanticRerank":false,"methodRerank":false}}""");
        _repo.StageAndCommit();
        var result = await CaptureAsync(() => FindFilesCommand.RunAsync(
            ["refund", "--task", "Find refund", "--explain", "--repo", _repo.Root],
            _ => throw new InvalidOperationException("should not load")));
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("method=-");
        result.Stderr.Should().BeEmpty();
    }

    [Fact]
    public async Task PathScopingPrecedesExtractionAndLiveEditsAreEmbedded()
    {
        _repo.WriteFile("src/In/Refund.cs", "class Refund { void Execute() { Send(); } }");
        _repo.WriteFile("src/Out/Refund.cs", "class Refund { void Execute() { OutOfScope(); } }");
        _repo.WriteFile(".ck.json", """{"findFiles":{"semanticRerank":false,"methodRerank":true}}""");
        _repo.StageAndCommit();
        _repo.WriteFile("src/In/Refund.cs", "class Refund { void Execute() { Retry(); } }");
        var embedder = new BehaviorEmbedder();
        var result = await CaptureAsync(() => FindFilesCommand.RunAsync(
            ["refund", "--task", "Find refund retries", "--path", "src/In", "--repo", _repo.Root], _ => embedder));
        result.ExitCode.Should().Be(0);
        embedder.Documents.Should().ContainSingle().Which.Should().Contain("Retry();").And.NotContain("OutOfScope");
        result.Stdout.Should().Contain("src/In/Refund.cs").And.NotContain("src/Out/");
    }

    private sealed class BehaviorEmbedder : IBatchTextEmbedder
    {
        public string? Query { get; private set; }
        public List<string> Documents { get; } = [];
        public float[] Embed(string text) { Query = text; return [1, 0]; }
        public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts)
        {
            Documents.AddRange(texts);
            return texts.Select(t => t.Contains("Retry();") ? new[] { 1f, 0f } : [-1f, 0f]).ToArray();
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCommand(params string[] args)
    {
        return await CaptureAsync(() => FindFilesCommand.RunAsync(args));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await action();
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private static (string Stdout, string Stderr) Capture(Action action)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            action();
            return (stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private void WriteClass(string path, string typeName, string methodName)
    {
        _repo.WriteFile(path, $$"""
            namespace Demo;

            public sealed class {{typeName}}
            {
                public void {{methodName}}() { }
            }
            """);
    }

    public void Dispose() => _repo.Dispose();

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ck-empty-model-" + System.IO.Path.GetRandomFileName());

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
