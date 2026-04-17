using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextKing.Core.Ast;

public enum SourceMode
{
    SignaturePlusBody,
    SignatureOnly,
    BodyOnly,
    BodyWithoutComments
}

/// <summary>
/// Extracts source content for a named member from a C# file, always live from disk.
/// Returns exact line and char-offset spans within the original file alongside the
/// requested content slice.
/// </summary>
public static class MethodSourceExtractor
{
    public static IReadOnlyList<MethodSourceResult> Extract(
        string filePath,
        string memberName,
        string? typeFilter,
        SourceMode mode)
    {
        var source  = File.ReadAllText(filePath);
        var tree    = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root    = tree.GetRoot();
        var lines   = tree.GetText().Lines;
        var results = new List<MethodSourceResult>();

        foreach (var node in root.DescendantNodes())
        {
            var name = GetMemberName(node);
            if (name is null || !name.Equals(memberName, StringComparison.Ordinal))
                continue;

            var containingType = GetContainingTypeName(node);
            if (typeFilter is not null &&
                !containingType.Equals(typeFilter, StringComparison.Ordinal) &&
                !containingType.EndsWith("." + typeFilter, StringComparison.Ordinal))
                continue;

            var (content, startChar, endChar) = ExtractContent(node, source, mode);
            if (content is null) continue; // no body exists for requested mode

            var startLine = lines.GetLineFromPosition(startChar).LineNumber + 1;
            var endLine   = lines.GetLineFromPosition(Math.Max(startChar, endChar - 1)).LineNumber + 1;

            results.Add(new MethodSourceResult(
                File:           filePath,
                MemberName:     name,
                ContainingType: containingType,
                Signature:      BuildSignature(node),
                Mode:           ModeLabel(mode),
                StartLine:      startLine,
                EndLine:        endLine,
                StartChar:      startChar,
                EndChar:        endChar,
                Content:        content));
        }

        return results;
    }

    /// <summary>
    /// Returns all member names in the file (methods, constructors, properties).
    /// Used for fuzzy-match suggestions when a member name is not found.
    /// </summary>
    public static IReadOnlyList<string> GetAllMemberNames(string filePath)
    {
        var source = File.ReadAllText(filePath);
        var tree   = CSharpSyntaxTree.ParseText(source, path: filePath);
        var root   = tree.GetRoot();

        return root.DescendantNodes()
            .Select(GetMemberName)
            .Where(n => n is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // ── Name / type helpers ──────────────────────────────────────────────────────

    private static string? GetMemberName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m      => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        PropertyDeclarationSyntax p    => p.Identifier.Text,
        _                              => null
    };

    private static string GetContainingTypeName(SyntaxNode node)
    {
        var parts = new List<string>();
        foreach (var ancestor in node.Ancestors().OfType<TypeDeclarationSyntax>())
            parts.Insert(0, ancestor.Identifier.Text);
        return parts.Count > 0 ? string.Join('.', parts) : "<global>";
    }

    private static string BuildSignature(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m =>
            $"{m.Modifiers} {m.ReturnType} {m.Identifier}{m.TypeParameterList}{m.ParameterList}".Trim(),
        ConstructorDeclarationSyntax c =>
            $"{c.Modifiers} {c.Identifier}{c.ParameterList}".Trim(),
        PropertyDeclarationSyntax p =>
            $"{p.Modifiers} {p.Type} {p.Identifier}".Trim(),
        _ => string.Empty
    };

    // ── Content extraction ───────────────────────────────────────────────────────

    private static (string? content, int startChar, int endChar) ExtractContent(
        SyntaxNode node, string source, SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly        => SignatureContent(node, source),
        SourceMode.SignaturePlusBody    => FullContent(node, source),
        SourceMode.BodyOnly            => BodyContent(node, source),
        SourceMode.BodyWithoutComments => BodyNoComments(node, source),
        _                              => FullContent(node, source)
    };

    private static (string content, int startChar, int endChar) FullContent(
        SyntaxNode node, string source)
    {
        var span = node.Span;
        return (source[span.Start..span.End], span.Start, span.End);
    }

    private static (string content, int startChar, int endChar) SignatureContent(
        SyntaxNode node, string source)
    {
        var bodyStart = GetBodyStart(node);
        int sigEnd;

        if (bodyStart >= 0)
        {
            // Trim trailing whitespace between signature tokens and body opener
            sigEnd = bodyStart;
            while (sigEnd > node.Span.Start && char.IsWhiteSpace(source[sigEnd - 1]))
                sigEnd--;
        }
        else
        {
            // Abstract / extern / interface member — full span (includes trailing semicolon)
            sigEnd = node.Span.End;
        }

        var start = node.Span.Start;
        return (source[start..sigEnd], start, sigEnd);
    }

    private static (string? content, int startChar, int endChar) BodyContent(
        SyntaxNode node, string source)
    {
        var body = GetBodyNode(node);
        if (body is null) return (null, node.Span.Start, node.Span.Start);
        var span = body.Span;
        return (source[span.Start..span.End], span.Start, span.End);
    }

    private static (string? content, int startChar, int endChar) BodyNoComments(
        SyntaxNode node, string source)
    {
        var body = GetBodyNode(node);
        if (body is null) return (null, node.Span.Start, node.Span.Start);

        var originalSpan = body.Span;

        // Replace comment trivia with nothing; all other trivia (whitespace, EOL) preserved.
        var stripped = body.ReplaceTrivia(
            body.DescendantTrivia(),
            (trivia, _) => IsCommentTrivia(trivia.Kind()) ? default : trivia);

        return (stripped.ToFullString(), originalSpan.Start, originalSpan.End);
    }

    // ── Body node / span helpers ─────────────────────────────────────────────────

    private static SyntaxNode? GetBodyNode(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m      when m.Body != null           => m.Body,
        MethodDeclarationSyntax m      when m.ExpressionBody != null => m.ExpressionBody,
        ConstructorDeclarationSyntax c when c.Body != null           => c.Body,
        ConstructorDeclarationSyntax c when c.ExpressionBody != null => c.ExpressionBody,
        PropertyDeclarationSyntax p    when p.AccessorList != null   => p.AccessorList,
        PropertyDeclarationSyntax p    when p.ExpressionBody != null => p.ExpressionBody,
        _ => null
    };

    private static int GetBodyStart(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m      when m.Body != null           => m.Body.Span.Start,
        MethodDeclarationSyntax m      when m.ExpressionBody != null => m.ExpressionBody.Span.Start,
        ConstructorDeclarationSyntax c when c.Body != null           => c.Body.Span.Start,
        ConstructorDeclarationSyntax c when c.ExpressionBody != null => c.ExpressionBody.Span.Start,
        PropertyDeclarationSyntax p    when p.AccessorList != null   => p.AccessorList.Span.Start,
        PropertyDeclarationSyntax p    when p.ExpressionBody != null => p.ExpressionBody.Span.Start,
        _ => -1
    };

    private static bool IsCommentTrivia(SyntaxKind kind) => kind is
        SyntaxKind.SingleLineCommentTrivia or
        SyntaxKind.MultiLineCommentTrivia or
        SyntaxKind.SingleLineDocumentationCommentTrivia or
        SyntaxKind.MultiLineDocumentationCommentTrivia;

    // ── Mode label ───────────────────────────────────────────────────────────────

    private static string ModeLabel(SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly        => "signature_only",
        SourceMode.SignaturePlusBody    => "signature_plus_body",
        SourceMode.BodyOnly            => "body_only",
        SourceMode.BodyWithoutComments => "body_without_comments",
        _                              => "signature_plus_body"
    };
}
