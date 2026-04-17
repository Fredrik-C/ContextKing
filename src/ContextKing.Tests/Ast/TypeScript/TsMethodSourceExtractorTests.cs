using ContextKing.Core.Ast;
using ContextKing.Core.Ast.TypeScript;
using FluentAssertions;

namespace ContextKing.Tests.Ast.TypeScript;

public class TsMethodSourceExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ck-ts-src-" + Path.GetRandomFileName());

    public TsMethodSourceExtractorTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Extract_SignaturePlusBody_ReturnsFullMethod()
    {
        var file = Write("svc.ts", """
            export class Svc {
                public processPayment(id: string): void {
                    console.log(id);
                }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "processPayment", null, SourceMode.SignaturePlusBody);

        results.Should().HaveCount(1);
        var r = results[0];
        r.MemberName.Should().Be("processPayment");
        r.ContainingType.Should().Be("Svc");
        r.Content.Should().Contain("processPayment");
        r.Content.Should().Contain("console.log");
    }

    [Fact]
    public void Extract_SignatureOnly_ReturnsOnlySignature()
    {
        var file = Write("svc.ts", """
            export class Svc {
                public processPayment(id: string): void {
                    console.log(id);
                }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "processPayment", null, SourceMode.SignatureOnly);

        results.Should().HaveCount(1);
        var r = results[0];
        r.Content.Should().Contain("processPayment");
        r.Content.Should().NotContain("console.log");
    }

    [Fact]
    public void Extract_BodyOnly_ReturnsOnlyBody()
    {
        var file = Write("svc.ts", """
            export class Svc {
                public processPayment(id: string): void {
                    console.log(id);
                }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "processPayment", null, SourceMode.BodyOnly);

        results.Should().HaveCount(1);
        var r = results[0];
        r.Content.Should().Contain("console.log");
        r.Content.Should().StartWith("{");
    }

    [Fact]
    public void Extract_BodyWithoutComments_StripsComments()
    {
        var file = Write("svc.ts", """
            export class Svc {
                doWork(): void {
                    // this is a comment
                    const x = 1; /* block comment */
                    return;
                }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "doWork", null, SourceMode.BodyWithoutComments);

        results.Should().HaveCount(1);
        var r = results[0];
        r.Content.Should().NotContain("// this is a comment");
        r.Content.Should().NotContain("block comment");
        r.Content.Should().Contain("const x = 1;");
    }

    [Fact]
    public void Extract_WithTypeFilter_FiltersCorrectly()
    {
        var file = Write("multi.ts", """
            export class A {
                doWork(): void { }
            }
            export class B {
                doWork(): void { }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "doWork", "A", SourceMode.SignaturePlusBody);

        results.Should().HaveCount(1);
        results[0].ContainingType.Should().Be("A");
    }

    [Fact]
    public void Extract_FunctionDeclaration_Works()
    {
        var file = Write("util.ts", """
            export function formatCurrency(value: number): string {
                return value.toFixed(2);
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "formatCurrency", null, SourceMode.SignaturePlusBody);

        results.Should().HaveCount(1);
        results[0].Content.Should().Contain("toFixed");
    }

    [Fact]
    public void Extract_InterfaceMethod_BodyOnly_ReturnsEmpty()
    {
        var file = Write("types.ts", """
            export interface IService {
                doWork(input: string): void;
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "doWork", null, SourceMode.BodyOnly);

        // method_signature has no body, so BodyOnly returns no results
        results.Should().BeEmpty();
    }

    [Fact]
    public void Extract_InterfaceMethod_SignatureOnly_Works()
    {
        var file = Write("types.ts", """
            export interface IService {
                doWork(input: string): void;
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "doWork", null, SourceMode.SignatureOnly);

        results.Should().HaveCount(1);
        results[0].Content.Should().Contain("doWork(input: string): void");
    }

    [Fact]
    public void Extract_ReturnsCorrectLineNumbers()
    {
        var file = Write("lines.ts", """
            export class Svc {
                firstMethod(): void { }

                secondMethod(): void { }
            }
            """);

        var results = TsMethodSourceExtractor.Extract(file, "secondMethod", null, SourceMode.SignaturePlusBody);

        results.Should().HaveCount(1);
        results[0].StartLine.Should().Be(4);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
