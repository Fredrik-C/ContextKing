using ContextKing.Core.Ast.TypeScript;

namespace ContextKing.Core.Ast.TreeSitter;

/// <summary>
/// TypeScript/TSX <see cref="ILanguageExtractor"/> implementation.
/// Uses the existing TypeScriptParser-backed extractors that already power the CLI.
/// </summary>
public sealed class TypeScriptExtractor : ILanguageExtractor
{
    public (IReadOnlyList<string> TypeNames, IReadOnlyList<string> MethodNames) ExtractTypeAndMethodNames(string path)
        => TsSignatureExtractor.ExtractTypeAndMethodNames(path);

    public void ExtractSignatures(IEnumerable<string> filePaths, TextWriter writer, TextWriter? errorWriter = null)
        => TsSignatureExtractor.Extract(filePaths, writer, errorWriter);

    public IReadOnlyList<MethodSourceResult> ExtractMethodSource(
        string filePath,
        string memberName,
        string? typeFilter,
        SourceMode mode)
        => TsMethodSourceExtractor.Extract(filePath, memberName, typeFilter, mode);

    public IReadOnlyList<MethodSourceResult> ExtractAllConstructors(
        string filePath,
        string? typeFilter,
        SourceMode mode)
        => TsMethodSourceExtractor.ExtractAllConstructors(filePath, typeFilter, mode);

    public IReadOnlyList<string> GetAllMemberNames(string filePath)
        => TsMethodSourceExtractor.GetAllMemberNames(filePath);

    public IReadOnlyList<string> ExtractPublicNamesFromFile(string filePath)
        => TsPublicMethodNameExtractor.ExtractFromFile(filePath);

    public IReadOnlyList<string> ExtractPublicNamesFromSource(string sourceText)
        => TsPublicMethodNameExtractor.Extract(sourceText);
}
