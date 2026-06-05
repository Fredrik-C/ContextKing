using ContextKing.Cli.Commands;
using ContextKing.Core.Knowledge;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public sealed class KnowledgeCommandTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public async Task Learn_WritesToSessionSpecificJsonlFile()
    {
        using var env = new EnvironmentVariableScope("CK_SESSION_ID", "engineer/session:42");

        var result = await CaptureAsync(() => LearnCommand.RunAsync([
            "--content", "Terminal refunds require provider retry handling.",
            "--folders", "src/Payments",
            "--repo", _repo.Root
        ]));

        result.ExitCode.Should().Be(0);
        var expectedPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "engineer-session-42");
        File.Exists(expectedPath).Should().BeTrue();
        File.Exists(KnowledgeStore.SnippetsPath(_repo.Root)).Should().BeFalse();
    }

    [Fact]
    public async Task RecallFolder_ReadsAllKnowledgeJsonlFiles()
    {
        var legacyPath = KnowledgeStore.SnippetsPath(_repo.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        await File.WriteAllTextAsync(legacyPath, """
            {"id":"legacy","content":"Legacy payment knowledge.","folders":["src/Payments"],"created_at":"2026-01-01T00:00:00Z"}

            """);

        var sessionPath = KnowledgeStore.SessionSnippetsPath(_repo.Root, "session-a");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        await File.WriteAllTextAsync(sessionPath, """
            {"id":"session","content":"Session payment knowledge.","folders":["src/Payments/Adyen"],"created_at":"2026-01-02T00:00:00Z"}

            """);

        var result = await CaptureAsync(() => RecallCommand.RunAsync([
            "--folder", "src/Payments/Adyen",
            "--repo", _repo.Root
        ]));

        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("Legacy payment knowledge.");
        result.Stdout.Should().Contain("Session payment knowledge.");
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

    public void Dispose() => _repo.Dispose();

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentVariableScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
