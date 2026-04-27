using ContextKing.Cli.Commands;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public class ReadFullFileCommandTests
{
    [Fact]
    public async Task RunAsync_ReadsSmallFile()
    {
        using var tempDir = new TempDir();
        var file = Path.Combine(tempDir.Path, "Sample.cs");
        await File.WriteAllTextAsync(file, "class Sample {}\n");

        var result = await CaptureConsoleAsync(() =>
            ReadFullFileCommand.RunAsync([file]));

        result.ExitCode.Should().Be(0);
        result.StdOut.Should().Contain("class Sample {}");
        result.StdErr.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_RefusesLargeFileWithoutOverride()
    {
        using var tempDir = new TempDir();
        var file = Path.Combine(tempDir.Path, "LargeFile.cs");
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Range(1, 8).Select(i => $"// line {i}")));

        var result = await CaptureConsoleAsync(() =>
            ReadFullFileCommand.RunAsync(["--max-lines", "3", file]));

        result.ExitCode.Should().Be(1);
        result.StdOut.Should().BeEmpty();
        result.StdErr.Should().Contain("Refused by guardrail");
        result.StdErr.Should().Contain("ck get-method-source");
        result.StdErr.Should().Contain("ck read-full-file --allow-large");
    }

    [Fact]
    public async Task RunAsync_AllowsLargeFileWithOverride()
    {
        using var tempDir = new TempDir();
        var file = Path.Combine(tempDir.Path, "LargeFile.ts");
        await File.WriteAllTextAsync(file, string.Join('\n', Enumerable.Range(1, 8).Select(i => $"// line {i}")));

        var result = await CaptureConsoleAsync(() =>
            ReadFullFileCommand.RunAsync(["--max-lines", "3", "--allow-large", file]));

        result.ExitCode.Should().Be(0);
        result.StdErr.Should().BeEmpty();
        result.StdOut.Should().Contain("// line 1");
        result.StdOut.Should().Contain("// line 8");
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> CaptureConsoleAsync(Func<Task<int>> run)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var outWriter = new StringWriter();
        using var errWriter = new StringWriter();

        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            var exitCode = await run();
            return (exitCode, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck-read-full-file-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for tests.
            }
        }
    }
}
