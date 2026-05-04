using ContextKing.Cli.Commands;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class ExpandFolderCommandTests
{
    [Fact]
    public async Task RunAsync_BroadPattern_ReturnsPagedShortlistInsteadOfError()
    {
        using var temp = new TempFolder();
        var dir = temp.Root;

        for (var i = 0; i < 12; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"Reconcil{i}.cs"), $$"""
                public class Reconcil{{i}}
                {
                    public void ReconcileA{{i}}() { }
                    public void ReconcileB{{i}}() { }
                    public void ReconcileC{{i}}() { }
                    public void ReconcileD{{i}}() { }
                    public void ReconcileE{{i}}() { }
                }
                """);
        }

        var (exit, stdout, stderr) = await CaptureAsync(
            () => ExpandFolderCommand.RunAsync(["--pattern", "Reconcil", "--limit", "5", dir]));

        exit.Should().Be(0);
        stderr.Should().Contain("[ck expand-folder] pagination:");
        stderr.Should().Contain("has_more=true");

        var fileLines = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(l => l.TrimEnd('\r').EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        fileLines.Should().Be(5);
    }

    [Fact]
    public async Task RunAsync_MaxSignatures_TruncatesPerFileOutput()
    {
        using var temp = new TempFolder();
        var dir = temp.Root;
        var file = Path.Combine(dir, "BigReconcil.cs");

        var members = string.Join(Environment.NewLine,
            Enumerable.Range(0, 30).Select(i => $"    public void Reconcile{i}() {{ }}"));

        File.WriteAllText(file, $$"""
            public class BigReconcil
            {
            {{members}}
            }
            """);

        var (exit, stdout, _) = await CaptureAsync(
            () => ExpandFolderCommand.RunAsync(["--pattern", "Reconcile", "--max-signatures", "5", dir]));

        exit.Should().Be(0);
        stdout.Should().Contain("... +");
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exit = await run();
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "ck-expand-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}

