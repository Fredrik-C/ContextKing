using ContextKing.Core.Ast;
using FluentAssertions;

namespace ContextKing.Tests.Ast;

public class MethodSourceExtractorTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ck-mse-" + Path.GetRandomFileName());

    // Fixed C# source with a mix of members, comments, and bodies
    private const string SampleSource = """
        public class PaymentService
        {
            private readonly string _apiKey;

            // Constructor
            public PaymentService(string apiKey)
            {
                _apiKey = apiKey; // store key
            }

            /// <summary>Processes a payment.</summary>
            public string ProcessPayment(string id, decimal amount)
            {
                // validate
                if (amount <= 0) throw new ArgumentException("amount");
                return $"ok:{id}"; /* return result */
            }

            public decimal Amount { get; set; }

            public abstract void AbstractMethod();
        }
        """;

    private readonly string _file;

    public MethodSourceExtractorTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "PaymentService.cs");
        File.WriteAllText(_file, SampleSource);
    }

    // ── SignaturePlusBody ─────────────────────────────────────────────────────

    [Fact]
    public void Extract_SignaturePlusBody_ContainsSignatureAndBody()
    {
        var results = Extract("ProcessPayment", SourceMode.SignaturePlusBody);

        results.Should().HaveCount(1);
        results[0].Content.Should().Contain("ProcessPayment");
        results[0].Content.Should().Contain("return");
        results[0].Content.Should().Contain("validate");
    }

    [Fact]
    public void Extract_SignaturePlusBody_ModeLabel_IsCorrect()
    {
        var result = Extract("ProcessPayment", SourceMode.SignaturePlusBody).Single();
        result.Mode.Should().Be("signature_plus_body");
    }

    // ── SignatureOnly ─────────────────────────────────────────────────────────

    [Fact]
    public void Extract_SignatureOnly_OmitsBody()
    {
        var result = Extract("ProcessPayment", SourceMode.SignatureOnly).Single();

        result.Content.Should().Contain("ProcessPayment");
        result.Content.Should().NotContain("{");
        result.Content.Should().NotContain("return");
    }

    [Fact]
    public void Extract_SignatureOnly_ModeLabel_IsCorrect()
    {
        var result = Extract("ProcessPayment", SourceMode.SignatureOnly).Single();
        result.Mode.Should().Be("signature_only");
    }

    // ── BodyOnly ──────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_BodyOnly_OmitsSignature()
    {
        var result = Extract("ProcessPayment", SourceMode.BodyOnly).Single();

        result.Content.Should().NotContain("public string ProcessPayment");
        result.Content.Should().Contain("return");
    }

    [Fact]
    public void Extract_BodyOnly_StartsAndEndsWithBraces()
    {
        var result   = Extract("ProcessPayment", SourceMode.BodyOnly).Single();
        var trimmed  = result.Content.Trim();

        trimmed.Should().StartWith("{");
        trimmed.Should().EndWith("}");
    }

    [Fact]
    public void Extract_BodyOnly_ModeLabel_IsCorrect()
    {
        var result = Extract("ProcessPayment", SourceMode.BodyOnly).Single();
        result.Mode.Should().Be("body_only");
    }

    // ── BodyWithoutComments ───────────────────────────────────────────────────

    [Fact]
    public void Extract_BodyWithoutComments_RemovesSingleLineComments()
    {
        var result = Extract("ProcessPayment", SourceMode.BodyWithoutComments).Single();

        result.Content.Should().NotContain("// validate");
        result.Content.Should().Contain("if (amount <= 0)");
    }

    [Fact]
    public void Extract_BodyWithoutComments_RemovesBlockComments()
    {
        var result = Extract("ProcessPayment", SourceMode.BodyWithoutComments).Single();

        result.Content.Should().NotContain("/* return result */");
        result.Content.Should().Contain("return");
    }

    [Fact]
    public void Extract_BodyWithoutComments_RemovesInlineTrailingComments()
    {
        var result = Extract("PaymentService", SourceMode.BodyWithoutComments).Single();

        result.Content.Should().NotContain("// store key");
        result.Content.Should().Contain("_apiKey = apiKey");
    }

    [Fact]
    public void Extract_BodyWithoutComments_ModeLabel_IsCorrect()
    {
        var result = Extract("ProcessPayment", SourceMode.BodyWithoutComments).Single();
        result.Mode.Should().Be("body_without_comments");
    }

    [Fact]
    public void Extract_BodyWithoutComments_PreservesStringLiterals()
    {
        // The string "$"ok:{id}"" contains characters that could be confused with
        // comment syntax — verify it is not stripped.
        var result = Extract("ProcessPayment", SourceMode.BodyWithoutComments).Single();
        result.Content.Should().Contain("ok:");
    }

    // ── No-body members ───────────────────────────────────────────────────────

    [Fact]
    public void Extract_AbstractMethod_BodyOnly_ReturnsEmpty()
    {
        // AbstractMethod has no body — BodyOnly mode should return no results
        var results = Extract("AbstractMethod", SourceMode.BodyOnly);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Extract_AbstractMethod_BodyWithoutComments_ReturnsEmpty()
    {
        var results = Extract("AbstractMethod", SourceMode.BodyWithoutComments);
        results.Should().BeEmpty();
    }

    // ── Type filter ───────────────────────────────────────────────────────────

    [Fact]
    public void Extract_TypeFilter_NarrowsToMatchingType()
    {
        var results = Extract("ProcessPayment", SourceMode.SignaturePlusBody,
            typeFilter: "PaymentService");
        results.Should().HaveCount(1);
        results[0].ContainingType.Should().Be("PaymentService");
    }

    [Fact]
    public void Extract_TypeFilter_NonMatchingType_ReturnsEmpty()
    {
        var results = Extract("ProcessPayment", SourceMode.SignaturePlusBody,
            typeFilter: "OtherService");
        results.Should().BeEmpty();
    }

    // ── Spans ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Extract_LineNumbers_AreOneBasedAndOrdered()
    {
        var result = Extract("ProcessPayment", SourceMode.SignaturePlusBody).Single();

        result.StartLine.Should().BeGreaterThan(0);
        result.EndLine.Should().BeGreaterOrEqualTo(result.StartLine);
    }

    [Fact]
    public void Extract_CharOffsets_AreNonNegativeAndOrdered()
    {
        var result = Extract("ProcessPayment", SourceMode.SignaturePlusBody).Single();

        result.StartChar.Should().BeGreaterOrEqualTo(0);
        result.EndChar.Should().BeGreaterThan(result.StartChar);
    }

    [Fact]
    public void Extract_CharOffsets_MatchContentSlice()
    {
        var result = Extract("ProcessPayment", SourceMode.SignaturePlusBody).Single();
        var source = File.ReadAllText(_file);
        var slice  = source[result.StartChar..result.EndChar];

        slice.Should().Be(result.Content);
    }

    // ── Unknown member ────────────────────────────────────────────────────────

    [Fact]
    public void Extract_UnknownMemberName_ReturnsEmpty()
    {
        var results = Extract("NonExistentMethod", SourceMode.SignaturePlusBody);
        results.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<MethodSourceResult> Extract(
        string memberName,
        SourceMode mode,
        string? typeFilter = null)
        => MethodSourceExtractor.Extract(_file, memberName, typeFilter, mode);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
