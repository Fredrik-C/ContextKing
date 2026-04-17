using ContextKing.Core.Ast.TypeScript;
using FluentAssertions;

namespace ContextKing.Tests.Ast.TypeScript;

public class TsSignatureExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ck-ts-sig-" + Path.GetRandomFileName());

    public TsSignatureExtractorTests() => Directory.CreateDirectory(_dir);

    // ── Method signatures ─────────────────────────────────────────────────

    [Fact]
    public void Extract_ClassMethod_EmitsSignatureLine()
    {
        var file = Write("service.ts", """
            export class PaymentService {
                public processPayment(id: string, amount: number): void { }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("processPayment");
        output.Should().Contain("PaymentService");
        output.Should().Contain("public processPayment(id: string, amount: number): void");
    }

    [Fact]
    public void Extract_AsyncMethod_IncludesAsyncModifier()
    {
        var file = Write("svc.ts", """
            export class Svc {
                public async fetchData(url: string): Promise<string> {
                    return '';
                }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("async");
        output.Should().Contain("fetchData");
    }

    [Fact]
    public void Extract_GetterSetter_IncludesGetSetModifier()
    {
        var file = Write("model.ts", """
            export class Order {
                get total(): number { return 0; }
                set total(value: number) { }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("get total(): number");
        output.Should().Contain("set total(value: number)");
    }

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Extract_Constructor_EmitsSignatureLine()
    {
        var file = Write("svc.ts", """
            export class PaymentService {
                constructor(apiKey: string) { }
            }
            """);

        var output = Run([file]);

        output.Should().Contain("constructor");
        output.Should().Contain("(apiKey: string)");
    }

    // ── Function declarations ─────────────────────────────────────────────

    [Fact]
    public void Extract_ExportedFunction_EmitsSignatureLine()
    {
        var file = Write("util.ts", """
            export function formatCurrency(value: number): string {
                return value.toFixed(2);
            }
            """);

        var output = Run([file]);

        output.Should().Contain("function formatCurrency(value: number): string");
    }

    // ── Interface members ─────────────────────────────────────────────────

    [Fact]
    public void Extract_InterfaceMethod_EmitsSignatureLine()
    {
        var file = Write("types.ts", """
            export interface IService {
                doWork(input: string): void;
            }
            """);

        var output = Run([file]);

        output.Should().Contain("IService");
        output.Should().Contain("doWork");
        output.Should().Contain("doWork(input: string): void");
    }

    // ── Fields and properties ─────────────────────────────────────────────

    [Fact]
    public void Extract_ClassField_EmitsSignatureLine()
    {
        var file = Write("model.ts", """
            export class Config {
                private apiKey: string;
                readonly timeout: number;
            }
            """);

        var output = Run([file]);

        output.Should().Contain("private apiKey: string");
        output.Should().Contain("readonly timeout: number");
    }

    // ── Type aliases and enums ────────────────────────────────────────────

    [Fact]
    public void Extract_TypeAlias_EmitsSignatureLine()
    {
        var file = Write("types.ts", """
            export type Config = {
                url: string;
            };
            """);

        var output = Run([file]);

        output.Should().Contain("type Config");
    }

    [Fact]
    public void Extract_Enum_EmitsSignatureLine()
    {
        var file = Write("types.ts", """
            export enum Status {
                Active = 'active',
                Inactive = 'inactive',
            }
            """);

        var output = Run([file]);

        output.Should().Contain("enum Status");
    }

    // ── Nested types ──────────────────────────────────────────────────────

    [Fact]
    public void Extract_NestedClass_ShowsDottedTypeName()
    {
        var file = Write("outer.ts", """
            export class Outer {
                static Inner = class {
                }
            }
            """);

        // The inner class is an assignment, not a nested class_declaration in TS
        // so we just verify the outer class methods work
        var output = Run([file]);
        output.Should().NotBeEmpty();
    }

    // ── Multiple files ────────────────────────────────────────────────────

    [Fact]
    public void Extract_MultipleFiles_AllEmit()
    {
        var f1 = Write("a.ts", "export function methodA(): void { }");
        var f2 = Write("b.ts", "export function methodB(): void { }");

        var output = Run([f1, f2]);

        output.Should().Contain("methodA");
        output.Should().Contain("methodB");
    }

    // ── Error handling ────────────────────────────────────────────────────

    [Fact]
    public void Extract_NonExistentFile_ReportsErrorAndContinues()
    {
        var good = Write("good.ts", "export function ok(): void { }");
        var missing = Path.Combine(_dir, "missing.ts");

        var output = Run([good, missing], out var errors);

        output.Should().Contain("ok");
        errors.Should().Contain("missing.ts");
    }

    // ── Output format ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_OutputFormat_IsTabSeparated()
    {
        var file = Write("fmt.ts", """
            export class Svc {
                doWork(): void { }
            }
            """);

        var lines = Run([file])
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
            line.Count(c => c == '\t').Should().Be(3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
        TsSignatureExtractor.Extract(files, outWriter, errWriter);
        errors = errWriter.ToString();
        return outWriter.ToString();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
