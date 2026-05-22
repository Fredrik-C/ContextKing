using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextKing.Core.Ast;

/// <summary>
/// <see cref="ILanguageExtractor"/> implementation for C# using Roslyn.
/// Delegates to the existing <see cref="SignatureExtractor"/>, <see cref="MethodSourceExtractor"/>,
/// and <see cref="PublicMethodNameExtractor"/> static methods.
/// </summary>
public sealed class CSharpRoslynExtractor : ILanguageExtractor
{
    public (IReadOnlyList<string> TypeNames, IReadOnlyList<string> MethodNames) ExtractTypeAndMethodNames(string path)
        => SignatureExtractor.ExtractTypeAndMethodNames(path);

    public void ExtractSignatures(IEnumerable<string> filePaths, TextWriter writer, TextWriter? errorWriter = null)
        => SignatureExtractor.Extract(filePaths, writer, errorWriter);

    public IReadOnlyList<MethodSourceResult> ExtractMethodSource(string filePath, string memberName, string? typeFilter, SourceMode mode)
        => MethodSourceExtractor.Extract(filePath, memberName, typeFilter, mode);

    public IReadOnlyList<MethodSourceResult> ExtractAllConstructors(string filePath, string? typeFilter, SourceMode mode)
        => MethodSourceExtractor.ExtractAllConstructors(filePath, typeFilter, mode);

    public IReadOnlyList<string> GetAllMemberNames(string filePath)
        => MethodSourceExtractor.GetAllMemberNames(filePath);

    public IReadOnlyList<string> ExtractPublicNamesFromFile(string filePath)
        => PublicMethodNameExtractor.ExtractFromFile(filePath);

    public IReadOnlyList<string> ExtractPublicNamesFromSource(string sourceText)
        => PublicMethodNameExtractor.Extract(sourceText);
}
