using System.Text;
using System.Text.RegularExpressions;
using LanguageRegistry = ContextKing.Core.Ast.LanguageRegistry;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TreeSitterLanguagePack;

namespace ContextKing.Core.SourceMap;

public sealed record MethodExtractionOptions(int CandidateFiles = 50, int MaxMethodsPerFile = 24,
    int MaxMethodsTotal = 500, int MaxCardChars = 6000, int MaxBodyChars = 3500,
    int LargeMethodThresholdChars = 8000, int LargeMethodExcerptChars = 800,
    int MaxMethodSourceChars = 20000, int DenseMethodThreshold = 100,
    int DenseMethodExcerptChars = 800, int DenseMethodMaxCards = 300);
public sealed record MethodExtractionResult(IReadOnlyList<MethodCandidateCard> Cards, int ParsedFiles, int Failures);

/// <summary>Parses only supplied lexical candidates. Cards and syntax trees are never persisted.</summary>
public sealed class MethodCandidateExtractor
{
    public MethodExtractionResult Extract(string repoRoot, IReadOnlyList<FileSearchHit> candidates,
        string query, string task, MethodExtractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var cards = new List<MethodCandidateCard>();
        var failures = 0;
        var parsed = 0;
        var root = Path.GetFullPath(repoRoot);
        var total = Math.Clamp(options.MaxMethodsTotal, 1, 2000);
        foreach (var hit in candidates.DistinctBy(h => h.Path.Replace('\\', '/'), StringComparer.Ordinal)
                     .Take(Math.Clamp(options.CandidateFiles, 1, 100)))
        {
            if (cancellationToken.IsCancellationRequested || cards.Count >= total) break;
            try
            {
                var path = Path.GetFullPath(Path.Combine(root, hit.Path));
                var relative = Path.GetRelativePath(root, path);
                if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
                    throw new IOException("Candidate outside repository.");
                if (!LanguageRegistry.IsSupported(path)) continue;
                // Bound parser work even when the working tree contains unexpectedly huge files.
                if (new FileInfo(path).Length > 2_000_000) throw new IOException("Candidate exceeds parse safety limit.");
                var source = File.ReadAllText(path);
                parsed++;
                var extracted = ExtractSourceWithDiagnostics(relative.Replace('\\', '/'), source, query, task, options, out var parseFailure);
                if (parseFailure) failures++;
                cards.AddRange(extracted.Take(total - cards.Count));
            }
            catch (Exception) { failures++; }
        }
        return new(ApplyDenseBodyLimit(cards, options), parsed, failures);
    }

    public IReadOnlyList<MethodCandidateCard> ExtractSource(string relativePath, string source,
        string query, string task, MethodExtractionOptions? options = null)
    {
        options ??= new();
        return ApplyDenseBodyLimit(ExtractSourceWithDiagnostics(relativePath, source, query, task, options, out _), options);
    }

    private static IReadOnlyList<MethodCandidateCard> ExtractSourceWithDiagnostics(string relativePath, string source,
        string query, string task, MethodExtractionOptions options, out bool parseFailure)
    {
        var lexicalTerms = Terms(query);
        var taskTerms = Terms(task);
        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        var cards = extension == ".cs"
            ? ExtractCSharp(relativePath, source, options, out parseFailure)
            : ExtractTree(relativePath, source, extension, options, out parseFailure);
        return cards.Where(c => !IsTrivial(c.BodyExcerpt) || Matches(c.MemberName, lexicalTerms) > 0)
            .OrderByDescending(c => Matches(c.MemberName + " " + c.ContainingType, lexicalTerms))
            .ThenByDescending(c => Matches(c.MemberName + " " + c.ContainingType, taskTerms))
            .ThenByDescending(c => Matches(c.StructuralSummary, lexicalTerms.Concat(taskTerms).Distinct().ToArray()))
            .ThenBy(c => c.StartLine)
            .Take(Math.Clamp(options.MaxMethodsPerFile, 1, 100))
            .Select(c => c with
            {
                Signature = MethodCandidateCard.Bound(c.Signature, 1000),
                StructuralSummary = MethodCandidateCard.Bound(c.StructuralSummary, 1500),
                BodyExcerpt = MethodCandidateCard.Bound(c.BodyExcerpt, Math.Clamp(options.MaxBodyChars, 0, 12000))
            }).ToArray();
    }

    private static IReadOnlyList<MethodCandidateCard> ExtractCSharp(string path, string source,
        MethodExtractionOptions options, out bool parseFailure)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        parseFailure = tree.GetDiagnostics().Any(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        var cards = new List<MethodCandidateCard>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            SyntaxNode? body = null;
            string? name = null;
            switch (node)
            {
                case BaseMethodDeclarationSyntax method:
                    body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                    name = method switch
                    {
                        MethodDeclarationSyntax m => m.Identifier.ValueText,
                        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
                        DestructorDeclarationSyntax d => d.Identifier.ValueText,
                        OperatorDeclarationSyntax o => "operator" + o.OperatorToken.ValueText,
                        ConversionOperatorDeclarationSyntax => "operator",
                        _ => null
                    };
                    break;
                case LocalFunctionStatementSyntax local:
                    body = (SyntaxNode?)local.Body ?? local.ExpressionBody; name = local.Identifier.ValueText; break;
                case AccessorDeclarationSyntax accessor:
                    body = (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody;
                    name = (accessor.Parent?.Parent as PropertyDeclarationSyntax)?.Identifier.ValueText + "." + accessor.Keyword.ValueText;
                    break;
                case PropertyDeclarationSyntax property when property.ExpressionBody is not null:
                    body = property.ExpressionBody; name = property.Identifier.ValueText; break;
            }
            if (body is null || name is null || node.ContainsDiagnostics) continue;
            var evidence = new List<string>();
            var summary = new List<string>();
            foreach (var child in node.DescendantNodes(n => n == node || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)))
            {
                switch (child)
                {
                    case InvocationExpressionSyntax invocation:
                        Add("call", invocation.Expression switch
                        {
                            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                            MemberBindingExpressionSyntax m => m.Name.Identifier.ValueText,
                            SimpleNameSyntax n => n.Identifier.ValueText,
                            _ => ""
                        }); break;
                    case CatchDeclarationSyntax caught: Add("catch", caught.Type.ToString()); break;
                    case ObjectCreationExpressionSyntax created: Add("new", created.Type.ToString()); break;
                    case AttributeSyntax attribute: Add("attribute", attribute.Name.ToString()); break;
                    case MemberAccessExpressionSyntax reference: Add("reference", reference.ToString()); break;
                    case CaseSwitchLabelSyntax label: Add("case", label.Value.ToString()); break;
                    case AwaitExpressionSyntax: Add("flow", "await"); break;
                    case ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax: Add("flow", "loop"); break;
                    case IfStatementSyntax or SwitchStatementSyntax: Add("flow", "branch"); break;
                    case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                        if (literal.Token.ValueText.Length <= 80) summary.Add("literal:" + literal.Token.ValueText);
                        break;
                }
            }
            var lines = tree.GetLineSpan(node.Span);
            var containingDeclaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var type = containingDeclaration?.Identifier.ValueText ?? "<global>";
            if (containingDeclaration?.BaseList is { } bases)
                foreach (var baseType in bases.Types) Add("base", baseType.Type.ToString());
            cards.Add(new(path, "CSharp", type, name, source[node.SpanStart..body.SpanStart].Trim(),
                string.Join('\n', summary.Distinct().Take(64)), Excerpt(source, node.SpanStart, node.Span.End, options),
                lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1, evidence.Distinct().Take(32).ToArray()));

            void Add(string kind, string value)
            {
                var item = SafeEvidence(kind, value);
                if (item is null) return;
                evidence.Add(item); summary.Add(item);
            }
        }
        return cards;
    }

    private static IReadOnlyList<MethodCandidateCard> ExtractTree(string path, string source, string extension,
        MethodExtractionOptions options, out bool parseFailure)
    {
        parseFailure = false;
        var language = extension switch { ".ts" => "typescript", ".tsx" => "tsx", ".py" => "python", ".kt" or ".kts" => "kotlin", _ => null };
        if (language is null) return [];
        using var parser = Parser.Default();
        parser.SetLanguage(language);
        using var tree = parser.Parse(source);
        if (tree is null) { parseFailure = true; return []; }
        parseFailure = tree.RootNode().HasError();
        var bytes = Encoding.UTF8.GetBytes(source);
        var cards = new List<MethodCandidateCard>();
        Walk(tree.RootNode(), "<global>", null, 0);
        return cards;

        string Slice(Node n) => Encoding.UTF8.GetString(bytes, checked((int)n.StartByte()), checked((int)(n.EndByte() - n.StartByte())));
        IEnumerable<Node> Children(Node n)
        {
            for (uint i = 0; i < n.ChildCount(); i++) if (n.Child(i) is { } child) yield return child;
        }
        string? Name(Node n) => n.ChildByFieldName("name") is { } named ? Slice(named) :
            Children(n).Where(c => c.Kind() is "identifier" or "simple_identifier" or "type_identifier" or "property_identifier").Select(Slice).FirstOrDefault();
        void Walk(Node node, string containingType, Node? parent, int depth)
        {
            if (depth > 256) return;
            var kind = node.Kind();
            if (kind is "class_declaration" or "class_definition" or "object_declaration" or "interface_declaration")
                containingType = Name(node) ?? containingType;
            if (IsCallable(kind))
            {
                var body = node.ChildByFieldName("body") ?? Children(node).FirstOrDefault(c => c.Kind() is "function_body" or "block" or "statement_block" or "statements");
                var name = Name(node) ?? (parent is null ? null : Name(parent));
                if (kind == "secondary_constructor") name = "constructor";
                if (kind is "getter" or "setter" && parent is not null)
                {
                    var property = parent.Kind() == "property_declaration" ? parent : Children(parent)
                        .LastOrDefault(c => c.Kind() == "property_declaration" && c.EndByte() <= node.StartByte());
                    var declaration = property is null ? null : Children(property).FirstOrDefault(c => c.Kind() == "variable_declaration");
                    var propertyName = declaration is null ? null : Name(declaration);
                    name = propertyName is null ? null : propertyName + (kind == "getter" ? ".get" : ".set");
                }
                if (body is not null && name is not null && !node.HasError() && !(language == "python" && IsPythonStub(body)))
                {
                    var evidence = new List<string>();
                    var summary = new List<string>();
                    var spanNode = parent?.Kind() == "decorated_definition" ? parent : node;
                    if (spanNode != node)
                        foreach (var decorator in Children(spanNode).Where(c => c.Kind() == "decorator")) Gather(decorator, 0);
                    // TypeScript places decorators either on the method or immediately before it.
                    if (parent?.Kind() == "class_body")
                    {
                        var pending = new List<Node>();
                        foreach (var sibling in Children(parent))
                        {
                            if (sibling.StartByte() >= node.StartByte()) break;
                            if (sibling.Kind() == "decorator") pending.Add(sibling);
                            else pending.Clear();
                        }
                        foreach (var decorator in pending) Gather(decorator, 0);
                    }
                    Gather(node, 0);
                    var signature = Encoding.UTF8.GetString(bytes, checked((int)spanNode.StartByte()), checked((int)(body.StartByte() - spanNode.StartByte()))).Trim();
                    var methodStart = checked((int)spanNode.StartByte());
                    var methodLength = checked((int)(spanNode.EndByte() - spanNode.StartByte()));
                    cards.Add(new(path, language, containingType, name, signature,
                        string.Join('\n', summary.Distinct().Take(64)), ExcerptUtf8(bytes, methodStart, methodLength, options),
                        checked((int)spanNode.StartPosition().Row + 1), checked((int)spanNode.EndPosition().Row + 1), evidence.Distinct().Take(32).ToArray()));

                    void Gather(Node n, int level)
                    {
                        if (level > 128) return;
                        var k = n.Kind();
                        if (level > 0 && (IsCallable(k) || k is "class_declaration" or "class_definition" or "lambda" or "lambda_literal")) return;
                        if (k is "decorator" or "annotation" or "marker_annotation")
                        {
                            var identifier = DescendantIdentifiers(n, 0).FirstOrDefault();
                            if (identifier is not null) Add("attribute", Slice(identifier));
                            return;
                        }
                        if (k is "call" or "call_expression")
                        {
                            var callable = n.ChildByFieldName("function") ?? Children(n).FirstOrDefault();
                            if (callable is not null) Add("call", Slice(callable));
                        }
                        if (k is "catch_clause" or "catch_block" or "except_clause")
                        {
                            var caught = n.ChildByFieldName("value") ?? n.ChildByFieldName("type");
                            if (caught is not null)
                            {
                                // Python except E as e: only E is evidence, never the alias or handler body.
                                if (caught.Kind() == "as_pattern") caught = Children(caught).FirstOrDefault();
                                if (caught is not null) foreach (var item in DescendantIdentifiers(caught, 0)) Add("catch", Slice(item));
                            }
                            else if (language == "kotlin")
                            {
                                var type = Children(n).FirstOrDefault(c => c.Kind() == "user_type");
                                if (type is not null) Add("catch", Slice(type));
                            }
                        }
                        if (k is "type_identifier" or "user_type") Add("reference", Slice(n));
                        if (k is "member_expression" or "navigation_expression" or "attribute") Add("reference", Slice(n));
                        if (k == "new_expression" && n.ChildByFieldName("constructor") is { } created) Add("new", Slice(created));
                        if (k is "string" or "string_literal" && Slice(n).Length <= 82) summary.Add("literal:" + Slice(n));
                        if (k is "await" or "await_expression") Add("flow", "await");
                        if (k is "for_statement" or "while_statement") Add("flow", "loop");
                        if (k is "if_statement" or "if_expression" or "switch_statement" or "when_expression") Add("flow", "branch");
                        foreach (var child in Children(n)) Gather(child, level + 1);
                    }
                    void Add(string category, string value)
                    {
                        var item = SafeEvidence(category, value);
                        if (item is null) return;
                        evidence.Add(item); summary.Add(item);
                    }
                }
            }
            foreach (var child in Children(node)) Walk(child, containingType, node, depth + 1);
        }
        bool IsPythonStub(Node body) => Children(body).All(n => n.Kind() is "comment" or "pass_statement" or "string" or "concatenated_string" or "ellipsis"
            || (n.Kind() == "expression_statement" && Children(n).All(c => c.Kind() is "string" or "concatenated_string" or "ellipsis")));
        IEnumerable<Node> DescendantIdentifiers(Node n, int depth)
        {
            if (depth > 16) yield break;
            if (n.Kind() is "identifier" or "type_identifier" or "simple_identifier") yield return n;
            foreach (var child in Children(n)) foreach (var item in DescendantIdentifiers(child, depth + 1)) yield return item;
        }
    }

    private static string Excerpt(string source, int start, int end, MethodExtractionOptions options)
    {
        var length = end - start;
        var maxSourceChars = Math.Clamp(options.MaxMethodSourceChars, 1, 1_000_000);
        if (length > maxSourceChars) return "";
        var threshold = Math.Clamp(options.LargeMethodThresholdChars, 1, maxSourceChars);
        if (length <= threshold) return source[start..end];
        return Excerpt(source, start, length, Math.Clamp(options.LargeMethodExcerptChars, 0, 12000));
    }

    private static string ExcerptUtf8(byte[] source, int start, int length, MethodExtractionOptions options)
    {
        var maxSourceChars = Math.Clamp(options.MaxMethodSourceChars, 1, 1_000_000);
        if (length > maxSourceChars) return "";
        var threshold = Math.Clamp(options.LargeMethodThresholdChars, 1, maxSourceChars);
        if (length <= threshold) return Encoding.UTF8.GetString(source, start, length);
        return MethodCandidateCard.Bound(Encoding.UTF8.GetString(source, start, length),
            Math.Clamp(options.LargeMethodExcerptChars, 0, 12000));
    }

    private static string Excerpt(string source, int start, int length, int maxChars)
    {
        if (maxChars <= 0) return "";
        if (length <= maxChars) return source.Substring(start, length);
        const string omission = "\n<omitted>\n";
        if (maxChars < omission.Length) return source.Substring(start, maxChars);
        var remaining = maxChars - omission.Length;
        var headLength = (remaining + 1) / 2;
        var tailLength = remaining / 2;
        return source.Substring(start, headLength) + omission + source.Substring(start + length - tailLength, tailLength);
    }

    private static IReadOnlyList<MethodCandidateCard> ApplyDenseBodyLimit(IReadOnlyList<MethodCandidateCard> cards,
        MethodExtractionOptions options)
    {
        var threshold = Math.Clamp(options.DenseMethodThreshold, 1, 2000);
        if (cards.Count <= threshold) return cards;
        var maxChars = Math.Clamp(options.DenseMethodExcerptChars, 0, 12000);
        var maxCards = Math.Clamp(options.DenseMethodMaxCards, threshold, 2000);
        return cards.Take(maxCards)
            .Select(card => card with { BodyExcerpt = MethodCandidateCard.Bound(card.BodyExcerpt, maxChars) })
            .ToArray();
    }

    private static bool IsCallable(string kind) => kind is "function_definition" or "function_declaration" or "method_definition"
        or "arrow_function" or "function_expression" or "secondary_constructor" or "getter" or "setter";

    // Only identifier chains enter explanations. Literals and arbitrary source never do.
    public static string? SafeEvidence(string category, string value) =>
        Regex.IsMatch(value, @"^[\p{L}_$][\p{L}\p{N}_$]*(?:\.[\p{L}_$][\p{L}\p{N}_$]*)*$", RegexOptions.CultureInvariant)
            ? category + ":" + (value.Length <= 100 ? value : value[..100]) : null;

    private static string[] Terms(string text) => PathTokenizer.TokenizeQuery(text).Where(t => t.Length >= 3).Distinct().ToArray();
    private static int Matches(string text, IReadOnlyList<string> terms) => terms.Count(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    private static bool IsTrivial(string text) => Regex.IsMatch(text,
        @"(?:\{\s*return\s+[\w.]+\s*;?\s*\}|=>\s*[\w.]+\s*;?|:\s*return\s+[\w.]+)\s*$", RegexOptions.CultureInvariant);
}
