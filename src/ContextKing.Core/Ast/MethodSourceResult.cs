namespace ContextKing.Core.Ast;

/// <summary>
/// A single member match returned by <see cref="MethodSourceExtractor.Extract"/>.
/// <para>
/// <see cref="StartChar"/> and <see cref="EndChar"/> are zero-based UTF-16 character offsets
/// within the original file, matching the span of the extracted content slice (not the full
/// member when mode narrows to signature or body only).
/// </para>
/// </summary>
public sealed record MethodSourceResult(
    string File,
    string MemberName,
    string ContainingType,
    string Signature,
    string Mode,
    int    StartLine,
    int    EndLine,
    int    StartChar,
    int    EndChar,
    string Content);
