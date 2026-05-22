using System.Text.Json;
using ContextKing.Cli.Commands;
using FluentAssertions;

namespace ContextKing.Tests.Commands;

public sealed class LanguageExtractionRegressionTests
{
    [Fact]
    public async Task GetEnumMembers_PythonEnumAndPlainClass_AreHandledCorrectly()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "sample.py");
        await File.WriteAllTextAsync(file, """
            from enum import Enum

            class Level(Enum):
                LOW = 1
                HIGH = 2

            class Plain:
                A = 1
                B = 2
            """);

        var level = await CaptureConsoleAsync(() => GetEnumMembersCommand.RunAsync([file, "Level"]));
        level.ExitCode.Should().Be(0);
        level.StdErr.Should().NotContain("ts_pack_core_ffi");

        using var levelJson = JsonDocument.Parse(level.StdOut);
        var members = levelJson.RootElement.GetProperty("members").EnumerateArray().Select(x => x.GetString()).ToArray();
        members.Should().Contain(["LOW", "HIGH"]);

        var plain = await CaptureConsoleAsync(() => GetEnumMembersCommand.RunAsync([file, "Plain"]));
        plain.ExitCode.Should().Be(1);
        plain.StdOut.Trim().Should().Be("{}");
        plain.StdErr.Should().Contain("No enum 'Plain'");
    }

    [Fact]
    public async Task GetConstructors_TypeScriptAndPython_ReturnExpectedConstructors()
    {
        using var temp = new TempDir();
        var tsFile = Path.Combine(temp.Path, "sample.ts");
        var pyFile = Path.Combine(temp.Path, "sample.py");

        await File.WriteAllTextAsync(tsFile, """
            export class Foo {
              constructor(private readonly id: number) {}
            }
            """);

        await File.WriteAllTextAsync(pyFile, """
            class Worker:
                def __init__(self, value):
                    self.value = value
            """);

        var ts = await CaptureConsoleAsync(() => GetConstructorsCommand.RunAsync([tsFile]));
        ts.ExitCode.Should().Be(0);
        ts.StdErr.Should().NotContain("ts_pack_core_ffi");
        using (var tsJson = JsonDocument.Parse(ts.StdOut))
        {
            var names = tsJson.RootElement.EnumerateArray()
                .Select(x => x.GetProperty("member_name").GetString())
                .Where(x => x is not null)
                .ToArray();
            names.Should().Contain("constructor");
        }

        var py = await CaptureConsoleAsync(() => GetConstructorsCommand.RunAsync([pyFile]));
        py.ExitCode.Should().Be(0);
        py.StdErr.Should().NotContain("ts_pack_core_ffi");
        using var pyJson = JsonDocument.Parse(py.StdOut);
        var pyNames = pyJson.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("member_name").GetString())
            .Where(x => x is not null)
            .ToArray();
        pyNames.Should().Contain("__init__");
    }

    [Fact]
    public async Task GetConstructors_Kotlin_ReturnsPrimaryAndSecondaryConstructors()
    {
        using var temp = new TempDir();
        var ktFile = Path.Combine(temp.Path, "sample.kt");
        await File.WriteAllTextAsync(ktFile, """
            class Service(val id: String) {
              constructor(): this("fallback")
            }
            """);

        var result = await CaptureConsoleAsync(() => GetConstructorsCommand.RunAsync([ktFile]));
        result.ExitCode.Should().Be(0);
        result.StdErr.Should().NotContain("ts_pack_core_ffi");

        using var json = JsonDocument.Parse(result.StdOut);
        var constructors = json.RootElement.EnumerateArray().ToArray();
        constructors.Should().HaveCount(2);
        constructors.Select(x => x.GetProperty("member_name").GetString()).Should().OnlyContain(x => x == "Service");
    }

    [Fact]
    public async Task GetUsings_Kotlin_LoadsTreeSitterRuntimeAndExtractsImports()
    {
        using var temp = new TempDir();
        var ktFile = Path.Combine(temp.Path, "imports.kt");
        await File.WriteAllTextAsync(ktFile, """
            import kotlinx.coroutines.*
            import kotlin.math.abs

            class Sample
            """);

        var result = await CaptureConsoleAsync(() => GetUsingsCommand.RunAsync([ktFile]));
        result.ExitCode.Should().Be(0);
        result.StdErr.Should().NotContain("ts_pack_core_ffi");
        result.StdOut.Should().Contain("import kotlinx.coroutines.*");
        result.StdOut.Should().Contain("import kotlin.math.abs");
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ck-language-tests", Guid.NewGuid().ToString("N"));
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
