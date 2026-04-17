using ContextKing.Core.Ast.TypeScript;
using FluentAssertions;

namespace ContextKing.Tests.Ast.TypeScript;

public class TsPublicMethodNameExtractorTests
{
    [Fact]
    public void Extract_ExportedFunction_ReturnsName()
    {
        var source = """
            export function processPayment(id: string): void { }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("processPayment");
    }

    [Fact]
    public void Extract_PublicClassMethod_ReturnsName()
    {
        var source = """
            export class Svc {
                public doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
    }

    [Fact]
    public void Extract_PrivateMethod_Excluded()
    {
        var source = """
            export class Svc {
                private internal(): void { }
                public doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
        names.Should().NotContain("internal");
    }

    [Fact]
    public void Extract_ProtectedMethod_Excluded()
    {
        var source = """
            export class Svc {
                protected helper(): void { }
                doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
        names.Should().NotContain("helper");
    }

    [Fact]
    public void Extract_DefaultVisibility_IsPublic()
    {
        // In TypeScript, methods without access modifiers are public by default
        var source = """
            export class Svc {
                doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
    }

    [Fact]
    public void Extract_Constructor_Excluded()
    {
        var source = """
            export class Svc {
                constructor() { }
                doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
        names.Should().NotContain("constructor");
    }

    [Fact]
    public void Extract_InterfaceMethod_Included()
    {
        var source = """
            export interface IService {
                doWork(input: string): void;
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("doWork");
    }

    [Fact]
    public void Extract_NonExportedFunction_Excluded()
    {
        var source = """
            function helper(): void { }
            export function publicFn(): void { }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Should().Contain("publicFn");
        names.Should().NotContain("helper");
    }

    [Fact]
    public void Extract_NoDuplicates()
    {
        var source = """
            export class A {
                doWork(): void { }
            }
            export class B {
                doWork(): void { }
            }
            """;

        var names = TsPublicMethodNameExtractor.Extract(source);

        names.Where(n => n == "doWork").Should().HaveCount(1);
    }
}
