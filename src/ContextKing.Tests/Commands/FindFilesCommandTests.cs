using ContextKing.Cli.Commands;
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
        _repo.WriteFile(".ck.json", """{ "findFiles": { "semanticRerank": false } }""");
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
