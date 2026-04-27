using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ContextKing.Core.Ast;

/// <summary>
/// Extracts public-surface names from a C# source file using Roslyn.
/// Lightweight alternative to <see cref="SignatureExtractor"/> — returns only method
/// names, type names, properties, constructors, and enum members for use as lexical keywords
/// in the source-map index.
/// </summary>
public static class PublicMethodNameExtractor
{
    /// <summary>
    /// Returns distinct public-surface names declared in <paramref name="sourceText"/>.
    /// Class/record members must be explicitly public; interface members and enum members are
    /// included because they are part of the public surface even without a public modifier.
    /// </summary>
    public static IReadOnlyList<string> Extract(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetRoot();

        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (IsPublicSurfaceType(type))
                Add(type.Identifier.Text);
        }

        foreach (var enumMember in root.DescendantNodes().OfType<EnumMemberDeclarationSyntax>())
        {
            if (enumMember.Parent is EnumDeclarationSyntax enumDeclaration && IsPublicSurfaceType(enumDeclaration))
                Add(enumMember.Identifier.Text);
        }

        foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
        {
            if (IsPublicMember(constructor))
                Add(constructor.Identifier.Text);
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!IsPublicMember(method))
                continue;
            Add(method.Identifier.Text);
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (!IsPublicMember(property))
                continue;
            Add(property.Identifier.Text);
        }

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Parent?.Parent is RecordDeclarationSyntax record && IsPublicSurfaceType(record))
                Add(parameter.Identifier.Text);
        }

        return names;

        void Add(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                names.Add(name);
        }
    }

    private static bool IsPublicSurfaceType(BaseTypeDeclarationSyntax type) =>
        type.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
        || type.Parent is InterfaceDeclarationSyntax;

    private static bool IsPublicMember(MemberDeclarationSyntax member) =>
        IsInPublicSurfaceContainer(member)
        && (member.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))
            || member.Parent is InterfaceDeclarationSyntax);

    private static bool IsInPublicSurfaceContainer(SyntaxNode node)
    {
        var containingType = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return containingType is null || IsPublicSurfaceType(containingType);
    }

    /// <summary>
    /// Reads the file at <paramref name="filePath"/> and extracts public method names.
    /// Returns an empty list if the file cannot be read.
    /// </summary>
    public static IReadOnlyList<string> ExtractFromFile(string filePath)
    {
        try
        {
            var source = File.ReadAllText(filePath);
            return Extract(source);
        }
        catch
        {
            return [];
        }
    }
}
