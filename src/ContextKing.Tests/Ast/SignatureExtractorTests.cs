using ContextKing.Core.Ast;
using FluentAssertions;

namespace ContextKing.Tests.Ast;

public class SignatureExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ck-sig-" + Path.GetRandomFileName());

    public SignatureExtractorTests() => Directory.CreateDirectory(_dir);

    // ── Method signatures ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_PublicMethod_EmitsSignatureLine()
    {
        var file = Write("Service.cs", """
            public class PaymentService
            {
                public void ProcessPayment(string id, decimal amount) { }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("ProcessPayment");
        output.Should().Contain("PaymentService");
        output.Should().Contain("void ProcessPayment(string id, decimal amount)");
    }

    [Fact]
    public void Extract_StaticMethod_IncludesStaticModifier()
    {
        var file = Write("Util.cs", """
            public class Util
            {
                public static string Format(decimal value) => value.ToString("C");
            }
            """);

        var output = Run([file]);

        output.Should().Contain("static");
        output.Should().Contain("Format");
    }

    [Fact]
    public void Extract_GenericMethod_IncludesTypeParameters()
    {
        var file = Write("Repo.cs", """
            public class Repository
            {
                public T Find<T>(int id) where T : class => default!;
            }
            """);

        var output = Run([file]);

        output.Should().Contain("Find<T>");
    }

    // ── Constructor signatures ────────────────────────────────────────────────

    [Fact]
    public void Extract_Constructor_EmitsSignatureLine()
    {
        var file = Write("Svc.cs", """
            public class PaymentService
            {
                public PaymentService(string apiKey) { }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("PaymentService");
        output.Should().Contain("(string apiKey)");
    }

    // ── Property signatures ───────────────────────────────────────────────────

    [Fact]
    public void Extract_Property_EmitsSignatureLine()
    {
        var file = Write("Model.cs", """
            public class Order
            {
                public decimal Amount { get; set; }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("Amount");
        output.Should().Contain("get;");
        output.Should().Contain("set;");
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    [Fact]
    public void Extract_NestedClass_ShowsDottedTypeName()
    {
        var file = Write("Outer.cs", """
            public class Outer
            {
                public class Inner
                {
                    public void Work() { }
                }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("Outer.Inner");
    }

    // ── Multiple files ────────────────────────────────────────────────────────

    [Fact]
    public void Extract_MultipleFiles_AllEmit()
    {
        var f1 = Write("A.cs", "public class A { public void MethodA() { } }");
        var f2 = Write("B.cs", "public class B { public void MethodB() { } }");

        var output = Run([f1, f2]);

        output.Should().Contain("MethodA");
        output.Should().Contain("MethodB");
    }

    // ── Error handling ────────────────────────────────────────────────────────

    [Fact]
    public void Extract_NonExistentFile_ReportsErrorAndContinues()
    {
        var good    = Write("Good.cs", "public class Good { public void Ok() { } }");
        var missing = Path.Combine(_dir, "Missing.cs");

        var output    = Run([good, missing], out var errors);

        output.Should().Contain("Ok");
        errors.Should().Contain("Missing.cs");
    }

    // ── Output format ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_OutputFormat_IsTabSeparated()
    {
        var file = Write("Fmt.cs", """
            public class Svc
            {
                public void DoWork() { }
            }
            """);

        var lines = Run([file])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Each line: filepath:line\tcontainingType\tmemberName\tsignature
        foreach (var line in lines)
            line.Count(c => c == '\t').Should().Be(3);
    }

    [Fact]
    public void Extract_LineNumber_IsOneBasedAndCorrect()
    {
        var file = Write("Lines.cs", """
            public class Svc
            {
                public void FirstMethod() { }

                public void SecondMethod() { }
            }
            """);

        var lines = Run([file])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // FirstMethod at line 3, SecondMethod at line 5
        var first  = lines.First(l => l.Contains("FirstMethod"));
        var second = lines.First(l => l.Contains("SecondMethod"));

        first.Should().Contain(":3\t");
        second.Should().Contain(":5\t");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Run(string[] files) => Run(files, out _);

    private string Run(string[] files, out string errors)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        SignatureExtractor.Extract(files, outWriter, errWriter);
        errors = errWriter.ToString();
        return outWriter.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
