using TreeSitterLanguagePack;

namespace ContextKing.Core.Ast.TreeSitter;

/// <summary>
/// Shared base class for tree-sitter language extractors (TypeScript, Kotlin, Python, etc.).
/// Subclasses declare language-specific node type names and signature builders;
/// the base class handles AST walking, content extraction, and output formatting.
/// </summary>
public abstract class TreeSitterExtractor : ILanguageExtractor
{
    // ── Language-specific properties (override in subclass) ────────────────────

    /// <summary>The language name passed to <c>parser.SetLanguage()</c>.</summary>
    protected abstract string LanguageName { get; }

    /// <summary>Node types that introduce a new containing-type scope.</summary>
    protected abstract string[] ContainerNodeTypes { get; }

    /// <summary>Node types that are treated as member/method declarations.</summary>
    protected abstract string[] MemberNodeTypes { get; }

    /// <summary>Node types that are treated as type declarations.</summary>
    protected abstract string[] TypeDeclarationNodeTypes { get; }

    /// <summary>The node type for top-level function declarations (not inside a class).</summary>
    protected abstract string FunctionDeclarationNodeType { get; }

    /// <summary>Return true if the member node has a visibility modifier that makes it non-public.</summary>
    protected abstract bool HasPrivateOrProtectedModifier(Node node, string source);

    /// <summary>Get the member name from a member node.</summary>
    protected abstract string? GetMemberName(Node node, string source);

    /// <summary>Build a human-readable signature for a member node.</summary>
    protected abstract string BuildMemberSignature(Node node, string source);

    /// <summary>
    /// Resolves a declaration name from node fields/children.
    /// Default implementation checks the "name" field then common identifier child kinds.
    /// </summary>
    protected virtual string? GetNodeName(Node node, string source)
    {
        var byField = GetFieldText(node, "name", source);
        if (!string.IsNullOrWhiteSpace(byField))
            return byField;

        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is null) continue;
            if (child.Kind() is "identifier" or "simple_identifier" or "type_identifier" or "property_identifier")
                return SourceSlice(source, child);
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="node"/> represents a constructor for this language.
    /// Implementations should set <paramref name="constructorName"/> to the user-facing member name.
    /// </summary>
    protected virtual bool TryMatchConstructor(
        Node node,
        string source,
        string containingType,
        out string constructorName)
    {
        constructorName = "constructor";
        if (node.Kind() != "method_definition")
            return false;

        var name = GetFieldText(node, "name", source);
        if (name == "constructor" || name is null)
        {
            constructorName = name ?? "constructor";
            return true;
        }

        return false;
    }

    // ── ILanguageExtractor implementation ─────────────────────────────────────

    public (IReadOnlyList<string> TypeNames, IReadOnlyList<string> MethodNames) ExtractTypeAndMethodNames(string path)
    {
        var source = File.ReadAllText(path);
        using var parser = CreateParser();
        var tree = parser.Parse(source);
        if (tree is null) return ([], []);
        var root = tree.RootNode();
        if (root is null) return ([], []);

        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var methodNames = new HashSet<string>(StringComparer.Ordinal);

        WalkForNames(root, source, typeNames, methodNames);
        return (typeNames.ToArray(), methodNames.ToArray());
    }

    public void ExtractSignatures(IEnumerable<string> filePaths, TextWriter writer, TextWriter? errorWriter = null)
    {
        errorWriter ??= Console.Error;

        foreach (var path in filePaths)
        {
            try
            {
                ExtractSignaturesFromFile(path, writer);
            }
            catch (Exception ex)
            {
                errorWriter.WriteLine($"[ck-signatures] WARN: skipping '{path}': {ex.Message}");
            }
        }
    }

    public IReadOnlyList<MethodSourceResult> ExtractMethodSource(string filePath, string memberName, string? typeFilter, SourceMode mode)
    {
        var source = File.ReadAllText(filePath);
        using var parser = CreateParser();
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var results = new List<MethodSourceResult>();
        FindMembers(root, source, filePath, memberName, typeFilter, mode, "<global>", results);
        return results;
    }

    public IReadOnlyList<MethodSourceResult> ExtractAllConstructors(string filePath, string? typeFilter, SourceMode mode)
    {
        var source = File.ReadAllText(filePath);
        using var parser = CreateParser();
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var results = new List<MethodSourceResult>();
        FindConstructors(root, source, filePath, typeFilter, mode, "<global>", results);
        return results;
    }

    public IReadOnlyList<string> GetAllMemberNames(string filePath)
    {
        var source = File.ReadAllText(filePath);
        using var parser = CreateParser();
        var tree = parser.Parse(source);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var names = new List<string>();
        CollectMemberNames(root, source, names);
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> ExtractPublicNamesFromFile(string filePath)
    {
        try
        {
            var source = File.ReadAllText(filePath);
            return ExtractPublicNamesFromSource(source);
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<string> ExtractPublicNamesFromSource(string sourceText)
    {
        using var parser = CreateParser();
        var tree = parser.Parse(sourceText);
        if (tree is null) return [];
        var root = tree.RootNode();
        if (root is null) return [];

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        WalkPublicNames(root, sourceText, isExported: false, names, seen);
        return names;
    }

    // ── Parser creation ───────────────────────────────────────────────────────

    private Parser CreateParser()
    {
        var parser = Parser.Default();
        parser.SetLanguage(LanguageName);
        return parser;
    }

    // ── Signature extraction ──────────────────────────────────────────────────

    private void ExtractSignaturesFromFile(string path, TextWriter writer)
    {
        var source = File.ReadAllText(path);
        using var parser = CreateParser();
        var tree = parser.Parse(source);
        if (tree is null) return;
        var root = tree.RootNode();
        if (root is null) return;

        WalkSignatures(root, source, path, "<global>", writer);
    }

    private void WalkSignatures(Node node, string source, string path, string containingType, TextWriter writer)
    {
        var kind = node.Kind();

        if (IsContainerNode(kind))
        {
            var name = GetNodeName(node, source) ?? "<anonymous>";
            var newContainer = containingType == "<global>" ? name : $"{containingType}.{name}";
            WalkChildren(node, child => WalkSignatures(child, source, path, newContainer, writer));
            return;
        }

        if (IsMemberNode(kind))
        {
            var name = GetMemberName(node, source) ?? "<unknown>";
            var sig = BuildMemberSignature(node, source);
            Emit(writer, path, node, containingType, name, sig);
            return;
        }

        WalkChildren(node, child => WalkSignatures(child, source, path, containingType, writer));
    }

    // ── Name extraction ───────────────────────────────────────────────────────

    private void WalkForNames(Node node, string source, HashSet<string> typeNames, HashSet<string> methodNames)
    {
        var kind = node.Kind();

        if (Array.IndexOf(TypeDeclarationNodeTypes, kind) >= 0)
        {
            var name = GetNodeName(node, source);
            if (!string.IsNullOrWhiteSpace(name))
                typeNames.Add(name);
        }
        else if (IsMemberNode(kind))
        {
            var name = GetMemberName(node, source);
            if (string.IsNullOrWhiteSpace(name) && kind is "method_definition")
                name = "constructor";
            if (!string.IsNullOrWhiteSpace(name))
                methodNames.Add(name);
        }

        WalkChildren(node, child => WalkForNames(child, source, typeNames, methodNames));
    }

    // ── Method source extraction ──────────────────────────────────────────────

    private delegate void NodeWalker(Node child);

    private void FindMembers(Node node, string source, string filePath, string memberName, string? typeFilter, SourceMode mode, string containingType, List<MethodSourceResult> results)
    {
        var kind = node.Kind();

        if (IsContainerNode(kind))
        {
            var typeName = GetNodeName(node, source) ?? "<anonymous>";
            var newContainer = containingType == "<global>" ? typeName : $"{containingType}.{typeName}";
            WalkChildren(node, child => FindMembers(child, source, filePath, memberName, typeFilter, mode, newContainer, results));
            return;
        }

        if (IsMemberNode(kind))
        {
            var name = GetMemberName(node, source);
            if (name is not null && name.Equals(memberName, StringComparison.Ordinal))
            {
                if (typeFilter is null ||
                    containingType.Equals(typeFilter, StringComparison.Ordinal) ||
                    containingType.EndsWith("." + typeFilter, StringComparison.Ordinal))
                {
                    var result = BuildResult(node, source, filePath, name, containingType, mode);
                    if (result is not null)
                        results.Add(result);
                }
            }
            return;
        }

        WalkChildren(node, child => FindMembers(child, source, filePath, memberName, typeFilter, mode, containingType, results));
    }

    private void FindConstructors(Node node, string source, string filePath, string? typeFilter, SourceMode mode, string containingType, List<MethodSourceResult> results)
    {
        var kind = node.Kind();

        if (IsContainerNode(kind))
        {
            var typeName = GetNodeName(node, source) ?? "<anonymous>";
            var newContainer = containingType == "<global>" ? typeName : $"{containingType}.{typeName}";
            WalkChildren(node, child => FindConstructors(child, source, filePath, typeFilter, mode, newContainer, results));
            return;
        }

        if (TryMatchConstructor(node, source, containingType, out var constructorName))
        {
            if (typeFilter is null ||
                containingType.Equals(typeFilter, StringComparison.Ordinal) ||
                containingType.EndsWith("." + typeFilter, StringComparison.Ordinal))
            {
                var result = BuildResult(node, source, filePath, constructorName, containingType, mode);
                if (result is not null)
                    results.Add(result);
            }
            return;
        }

        WalkChildren(node, child => FindConstructors(child, source, filePath, typeFilter, mode, containingType, results));
    }

    private void CollectMemberNames(Node node, string source, List<string> names)
    {
        var kind = node.Kind();
        if (IsMemberNode(kind))
        {
            var name = GetMemberName(node, source);
            if (name is not null)
                names.Add(name);
            return;
        }

        WalkChildren(node, child => CollectMemberNames(child, source, names));
    }

    // ── Public name extraction ────────────────────────────────────────────────

    private void WalkPublicNames(Node node, string source, bool isExported, List<string> names, HashSet<string> seen)
    {
        var kind = node.Kind();

        if (kind == "export_statement")
        {
            WalkChildren(node, child => WalkPublicNames(child, source, isExported: true, names, seen));
            return;
        }

        if (kind == FunctionDeclarationNodeType && isExported)
        {
            AddPublicName(node, source, names, seen);
            return;
        }

        if (Array.IndexOf(TypeDeclarationNodeTypes, kind) >= 0)
        {
            if (isExported)
                AddPublicName(node, source, names, seen);
        }

        if (kind is "method_definition" or "public_field_definition")
        {
            if (!HasPrivateOrProtectedModifier(node, source))
                AddPublicName(node, source, names, seen);
            return;
        }

        if (kind is "method_signature" or "property_signature")
        {
            AddPublicName(node, source, names, seen);
            return;
        }

        if (kind is "enum_assignment")
        {
            AddPublicName(node, source, names, seen);
            return;
        }

        WalkChildren(node, child => WalkPublicNames(child, source, isExported, names, seen));
    }

    private void AddPublicName(Node node, string source, List<string> names, HashSet<string> seen)
    {
        var name = GetNodeName(node, source);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name is "constructor") return;
        if (seen.Add(name))
            names.Add(name);
    }

    // ── Result building ───────────────────────────────────────────────────────

    private MethodSourceResult? BuildResult(Node node, string source, string filePath, string memberName, string containingType, SourceMode mode)
    {
        var (content, startChar, endChar) = ExtractContent(node, source, mode);
        if (content is null) return null;

        var startLine = CountLine(source, startChar);
        var endLine = CountLine(source, Math.Max(startChar, endChar - 1));

        return new MethodSourceResult(
            File: filePath,
            MemberName: memberName,
            ContainingType: containingType,
            Signature: BuildMemberSignature(node, source),
            Mode: ModeLabel(mode),
            StartLine: startLine,
            EndLine: endLine,
            StartChar: startChar,
            EndChar: endChar,
            Content: content);
    }

    // ── Content extraction ────────────────────────────────────────────────────

    private static (string? content, int startChar, int endChar) ExtractContent(Node node, string source, SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly => SignatureContent(node, source),
        SourceMode.SignaturePlusBody => FullContent(node, source),
        SourceMode.BodyOnly => BodyContent(node, source),
        SourceMode.BodyWithoutComments => BodyNoComments(node, source),
        _ => FullContent(node, source)
    };

    private static (string content, int startChar, int endChar) FullContent(Node node, string source)
    {
        var start = (int)node.StartByte();
        var end = (int)node.EndByte();
        return (source[start..end], start, end);
    }

    private static (string content, int startChar, int endChar) SignatureContent(Node node, string source)
    {
        var body = node.ChildByFieldName("body");
        int start = (int)node.StartByte();

        if (body is not null)
        {
            var sigEnd = (int)body.StartByte();
            while (sigEnd > start && char.IsWhiteSpace(source[sigEnd - 1]))
                sigEnd--;
            return (source[start..sigEnd], start, sigEnd);
        }

        var end = (int)node.EndByte();
        return (source[start..end], start, end);
    }

    private static (string? content, int startChar, int endChar) BodyContent(Node node, string source)
    {
        var body = node.ChildByFieldName("body");
        if (body is null)
        {
            var s = (int)node.StartByte();
            return (null, s, s);
        }

        var start = (int)body.StartByte();
        var end = (int)body.EndByte();
        return (source[start..end], start, end);
    }

    private static (string? content, int startChar, int endChar) BodyNoComments(Node node, string source)
    {
        var body = node.ChildByFieldName("body");
        if (body is null)
        {
            var s = (int)node.StartByte();
            return (null, s, s);
        }

        var start = (int)body.StartByte();
        var end = (int)body.EndByte();
        var bodyText = source[start..end];
        var stripped = StripComments(bodyText);
        return (stripped, start, end);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    protected static string? GetFieldText(Node node, string fieldName, string source)
    {
        var child = node.ChildByFieldName(fieldName);
        return child is null ? null : SourceSlice(source, child);
    }

    protected static string SourceSlice(string source, Node node)
    {
        var start = (int)node.StartByte();
        var end = (int)node.EndByte();
        return source[start..end];
    }

    private static void WalkChildren(Node node, NodeWalker action)
    {
        var count = node.ChildCount();
        for (uint i = 0; i < count; i++)
        {
            var child = node.Child(i);
            if (child is not null)
                action(child);
        }
    }

    private bool IsContainerNode(string kind) => Array.IndexOf(ContainerNodeTypes, kind) >= 0;

    private bool IsMemberNode(string kind) => Array.IndexOf(MemberNodeTypes, kind) >= 0 || kind == FunctionDeclarationNodeType;

    private static void Emit(TextWriter writer, string path, Node node, string containingType, string memberName, string signature)
    {
        var line = (int)node.StartPosition().Row + 1;
        var normalizedPath = path.Replace('\\', '/');
        writer.WriteLine($"{normalizedPath}:{line}\t{containingType}\t{memberName}\t{signature}");
    }

    private static int CountLine(string source, int charOffset)
    {
        var line = 1;
        for (var i = 0; i < charOffset && i < source.Length; i++)
        {
            if (source[i] == '\n') line++;
        }
        return line;
    }

    private static string ModeLabel(SourceMode mode) => mode switch
    {
        SourceMode.SignatureOnly => "signature_only",
        SourceMode.SignaturePlusBody => "signature_plus_body",
        SourceMode.BodyOnly => "body_only",
        SourceMode.BodyWithoutComments => "body_without_comments",
        _ => "signature_plus_body"
    };

    /// <summary>
    /// Simple comment stripper. Removes // line comments and /* block comments */ while
    /// preserving strings and template literals.
    /// </summary>
    internal static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] is '\'' or '"' or '`')
            {
                var quote = text[i];
                sb.Append(text[i++]);
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                        sb.Append(text[i++]);
                    sb.Append(text[i++]);
                }
                if (i < text.Length) sb.Append(text[i++]);
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
            {
                i += 2;
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                if (i + 1 < text.Length) i += 2;
                continue;
            }

            sb.Append(text[i++]);
        }

        return sb.ToString();
    }
}
