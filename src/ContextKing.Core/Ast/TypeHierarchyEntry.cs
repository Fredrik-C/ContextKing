namespace ContextKing.Core.Ast;

/// <summary>
/// A single type declaration with its base types and interfaces extracted from a source file.
/// </summary>
public sealed record TypeHierarchyEntry(
    string File,
    string Name,
    string Kind,                           // "class", "interface", "struct", "record", "enum"
    IReadOnlyList<string> BaseTypes,       // base class + interfaces; not distinguished without compilation
    int Line);
