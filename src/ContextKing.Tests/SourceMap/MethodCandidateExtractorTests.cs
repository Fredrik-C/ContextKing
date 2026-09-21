using ContextKing.Core.SourceMap;
using FluentAssertions;
using ContextKing.Tests.Helpers;

namespace ContextKing.Tests.SourceMap;

public class MethodCandidateExtractorTests
{
    [Theory]
    [InlineData("cs", "class Refund { void Execute() { try { Retry(); } catch (TimeoutException) { Recover(); } } }", "Execute")]
    [InlineData("ts", "class Refund { execute() { try { retry(); } catch (error) { recover(); } } }", "execute")]
    [InlineData("tsx", "class Refund { execute() { retry(); return <div/>; } }", "execute")]
    [InlineData("kt", "class Refund { fun execute() { try { retry() } catch (e: TimeoutException) { recover() } } }", "execute")]
    [InlineData("py", "class Refund:\n    def execute(self):\n        try:\n            retry()\n        except TimeoutException:\n            recover()\n", "execute")]
    public void ExtractsLiveCallableWithTypePathAndCalls(string extension, string source, string member)
    {
        var path = "src/Refund." + extension;
        var cards = new MethodCandidateExtractor().ExtractSource(path, source, "refund", "Find retry recovery");
        var card = cards.Should().ContainSingle().Subject;
        card.MemberName.Should().Be(member);
        card.ContainingType.Should().Be("Refund");
        card.FilePath.Should().Be(path);
        card.Signature.Should().Contain(member);
        card.BodyExcerpt.Should().Contain(extension == "cs" ? "Retry" : "retry");
        card.Evidence.Should().Contain(e => e.StartsWith("call:"));
        card.StartLine.Should().BeGreaterThan(0);
        card.EndLine.Should().BeGreaterThanOrEqualTo(card.StartLine);
        if (extension is "cs" or "kt" or "py") card.Evidence.Should().Contain("catch:TimeoutException");
    }

    [Theory]
    [InlineData("ts", "interface I { execute(): void; value: number; }")]
    [InlineData("kt", "interface I { fun execute(): Unit }")]
    public void SkipsBodylessDeclarations(string extension, string source)
    {
        new MethodCandidateExtractor().ExtractSource("src/Test." + extension, source, "execute", "Find execution")
            .Should().BeEmpty();
    }

    [Fact]
    public void CardsCSharpInterfaceMethodWithoutBody()
    {
        var cards = new MethodCandidateExtractor().ExtractSource(
            "src/Test.cs", "interface I { void Execute(int count); int Value { get; set; } }", "execute", "Find execution");

        var card = cards.Should().ContainSingle().Subject;
        card.MemberName.Should().Be("Execute");
        card.ContainingType.Should().Be("I");
        card.Signature.Should().Be("void Execute(int count)");
        card.BodyExcerpt.Should().BeEmpty();
    }

    [Fact]
    public void SelectionPrioritizesNamesAndBoundsBodies()
    {
        var source = "class A { void Other() { Run(); } void Retry() { Run(); Run(); Run(); } }";
        var cards = new MethodCandidateExtractor().ExtractSource("A.cs", source, "retry", "Find recovery",
            new(MaxMethodsPerFile: 1, MaxBodyChars: 20));
        cards.Should().ContainSingle().Which.MemberName.Should().Be("Retry");
        cards[0].BodyExcerpt.Length.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void LargeMethodsUseSmallHeadAndTailExcerpts()
    {
        var source = "class A { void Retry() { Start(); " + new string('x', 400) + " Finish(); } }";
        var cards = new MethodCandidateExtractor().ExtractSource("A.cs", source, "retry", "Find recovery",
            new(LargeMethodThresholdChars: 100, LargeMethodExcerptChars: 80, MaxMethodSourceChars: 1000));

        var card = cards.Should().ContainSingle().Subject;
        card.BodyExcerpt.Length.Should().BeLessThanOrEqualTo(80);
        card.BodyExcerpt.Should().Contain("Start()").And.Contain("Finish()").And.Contain("<omitted>");
    }

    [Fact]
    public void ExtremelyLargeMethodsKeepStructuralDataButSkipBodyExcerpt()
    {
        var source = "class A { void Retry() { Start(); " + new string('x', 400) + " Finish(); } }";
        var cards = new MethodCandidateExtractor().ExtractSource("A.cs", source, "retry", "Find recovery",
            new(LargeMethodThresholdChars: 100, LargeMethodExcerptChars: 80, MaxMethodSourceChars: 200));

        var card = cards.Should().ContainSingle().Subject;
        card.MemberName.Should().Be("Retry");
        card.Signature.Should().Contain("Retry");
        card.Evidence.Should().Contain("call:Start");
        card.BodyExcerpt.Should().BeEmpty();
    }

    [Fact]
    public void DenseMethodSetsUseSmallHeadAndTailExcerpts()
    {
        var methods = string.Join("\n", Enumerable.Range(0, 3)
            .Select(i => $"void Retry{i}() {{ Start{i}(); {new string('x', 120)} Finish{i}(); }}"));
        var cards = new MethodCandidateExtractor().ExtractSource("A.cs", "class A { " + methods + " }", "retry", "Find recovery",
            new(DenseMethodThreshold: 2, DenseMethodExcerptChars: 80, DenseMethodMaxCards: 2));

        cards.Should().HaveCount(2);
        cards.Should().OnlyContain(card => card.BodyExcerpt.Length <= 80 && card.BodyExcerpt.Contains("<omitted>"));
        cards.Should().OnlyContain(card => card.BodyExcerpt.Contains("Start") && card.BodyExcerpt.Contains("Finish"));
    }

    [Theory]
    [InlineData("cs", "class A { void Broken( { retry(")]
    [InlineData("ts", "class A { broken( { retry(")]
    [InlineData("kt", "class A { fun broken( { retry(")]
    [InlineData("py", "class A:\n def broken(:\n retry(")]
    public void IncompleteSourceDoesNotCrash(string extension, string source)
    {
        var extract = () => new MethodCandidateExtractor().ExtractSource("A." + extension, source, "retry", "Find retries");
        extract.Should().NotThrow();
    }

    [Fact]
    public void ExplainEvidenceExcludesLiteralsAndExpressions()
    {
        var cards = new MethodCandidateExtractor().ExtractSource("A.cs", "class A { void Send() { Log(\"secret-value\"); Get(\"secret\").Send(); } }", "send", "Find send");
        string.Join(",", cards.Single().Evidence).Should().NotContain("secret").And.NotContain("(");
        MethodCandidateExtractor.SafeEvidence("call", "Run(\"secret\")").Should().BeNull();
    }

    [Theory]
    [InlineData("py", "class Refund:\n    def execute(self):\n        pass\n    def other(self):\n        ...\n")]
    [InlineData("py", "from abc import abstractmethod\nclass Refund:\n    @abstractmethod\n    def execute(self):\n        \"\"\"Contract only.\"\"\"\n        pass\n")]
    public void PythonStubsDoNotBecomeExecutableCards(string extension, string source)
    {
        new MethodCandidateExtractor().ExtractSource("A." + extension, source, "execute", "Find recovery")
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("cs", "class A { A() { Initialize(); } void Execute() { void Retry() { Recover(); } Retry(); } int Value { get { return Compute(); } } }", "A", "Retry", "Value.get")]
    [InlineData("ts", "class A { constructor() { initialize(); } execute() { function retry() { recover(); } retry(); } get value() { return compute(); } }", "constructor", "retry", "value")]
    [InlineData("kt", "class A {\n constructor() { initialize() }\n fun execute() {\n fun retry() { recover() }\n retry()\n }\n val value: Int\n get() = compute()\n}", "constructor", "retry", "value.get")]
    [InlineData("py", "class A:\n    def __init__(self):\n        initialize()\n    def execute(self):\n        def retry():\n            recover()\n        retry()\n    @property\n    def value(self):\n        return compute()\n", "__init__", "retry", "value")]
    public void ConstructorsLocalFunctionsAndNontrivialAccessorsAreSupported(string extension, string source, string constructor, string local, string accessor)
    {
        var cards = new MethodCandidateExtractor().ExtractSource("A." + extension, source, "retry", "Find recovery");
        cards.Select(c => c.MemberName).Should().Contain([constructor, local, accessor]);
    }

    [Theory]
    [InlineData("cs", "class A { void Execute() { void Local() { SecretInner(); } Outer(); } }", "Execute", "call:SecretInner")]
    [InlineData("ts", "function execute() { function local() { secretInner(); } outer(); }", "execute", "call:secretInner")]
    [InlineData("kt", "fun execute() { fun local() { secretInner() }; outer() }", "execute", "call:secretInner")]
    [InlineData("py", "def execute():\n    def local():\n        secret_inner()\n    outer()\n", "execute", "call:secret_inner")]
    public void NestedCallableEvidenceIsNotAttributedToOuterMember(string extension, string source, string member, string excluded)
    {
        var card = new MethodCandidateExtractor().ExtractSource("A." + extension, source, "execute", "Find outer operation")
            .Single(c => c.MemberName == member);
        card.Evidence.Should().NotContain(excluded);
    }

    [Theory]
    [InlineData("cs", "class A : Base { [Audit] void Execute() { switch (channel) { case Channel.Terminal: new Refund(); break; } } }", "attribute:Audit", "reference:Channel.Terminal")]
    [InlineData("ts", "class A extends Base { @audit execute() { const value = new Refund(); switch (channel) { case Channel.Terminal: send(); } } }", "attribute:audit", "reference:Channel.Terminal")]
    [InlineData("kt", "class A : Base() { @Audit fun execute() { val value = Refund(); when (channel) { Channel.Terminal -> send() } } }", "attribute:Audit", "reference:Channel.Terminal")]
    [InlineData("py", "class A(Base):\n    @audit\n    def execute(self):\n        value = Refund()\n        if channel == Channel.Terminal:\n            send()\n", "attribute:audit", "reference:Channel.Terminal")]
    public void DecoratorsAndEnumReferencesAreStructuralEvidence(string extension, string source, string attribute, string reference)
    {
        var cards = new MethodCandidateExtractor().ExtractSource("A." + extension, source, "execute", "Find terminal handling");
        cards.Should().ContainSingle().Which.Evidence.Should().Contain(attribute).And.Contain(reference);
    }

    [Fact]
    public void CandidatePathsAreDeduplicatedAndLimitsAreDeterministic()
    {
        using var repo = new TempRepo();
        repo.WriteFile("src/A.cs", "class A { void First() { Retry(); } void Second() { Retry(); } }");
        repo.WriteFile("src/B.cs", "class B { void Third() { Retry(); } void Fourth() { Retry(); } }");
        repo.WriteFile("src/C.cs", "class C { void Fifth() { Retry(); } }");
        var candidates = new[] { Hit("src/A.cs"), Hit("src\\A.cs"), Hit("src/B.cs"), Hit("src/C.cs") };
        var extractor = new MethodCandidateExtractor();
        var options = new MethodExtractionOptions(CandidateFiles: 2, MaxMethodsPerFile: 2, MaxMethodsTotal: 3);
        var result = extractor.Extract(repo.Root, candidates, "retry", "Find retries", options);
        result.ParsedFiles.Should().Be(2);
        result.Cards.Select(c => c.MemberName).Should().Equal("First", "Second", "Third");
        extractor.Extract(repo.Root, candidates, "retry", "Find retries", options).Cards.Select(c => c.MemberName)
            .Should().Equal(result.Cards.Select(c => c.MemberName));
        var invalid = extractor.Extract(repo.Root, [Hit("../outside.cs"), Hit("missing.cs"), Hit("src/B.cs")], "retry", "Find retries");
        invalid.Failures.Should().Be(2);
        invalid.Cards.Should().HaveCount(2);
    }

    private static FileSearchHit Hit(string path) => new(path, 1, 1, null, 1, 1, "src", path, "", "", "", []);
}
