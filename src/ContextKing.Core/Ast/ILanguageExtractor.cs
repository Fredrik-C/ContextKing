namespace ContextKing.Core.Ast;

public interface ILanguageExtractor
{
    (IReadOnlyList<string> TypeNames, IReadOnlyList<string> MethodNames) ExtractTypeAndMethodNames(string path);

    void ExtractSignatures(
        IEnumerable<string> filePaths,
        TextWriter writer,
        TextWriter? errorWriter = null);

    IReadOnlyList<MethodSourceResult> ExtractMethodSource(
        string filePath,
        string memberName,
        string? typeFilter,
        SourceMode mode);

    IReadOnlyList<MethodSourceResult> ExtractAllConstructors(
        string filePath,
        string? typeFilter,
        SourceMode mode);

    IReadOnlyList<string> GetAllMemberNames(string filePath);

    IReadOnlyList<string> ExtractPublicNamesFromFile(string filePath);

    IReadOnlyList<string> ExtractPublicNamesFromSource(string sourceText);
}
