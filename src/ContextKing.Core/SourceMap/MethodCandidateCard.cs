namespace ContextKing.Core.SourceMap;

/// <summary>Ephemeral inference input. BodyExcerpt must never be used in diagnostics.</summary>
public sealed record MethodCandidateCard(
    string FilePath,
    string Language,
    string ContainingType,
    string MemberName,
    string Signature,
    string StructuralSummary,
    string BodyExcerpt,
    int StartLine,
    int EndLine,
    IReadOnlyList<string> Evidence)
{
    public string ToEmbeddingText(int maxChars = 6000, int maxBodyChars = 3500)
    {
        maxChars = Math.Clamp(maxChars, 128, 16000);
        var header = $"Language: {Language}\nPath: {FilePath}\nType: {ContainingType}\nMember: {MemberName}\nSignature: {Signature}\nStructure:\n{StructuralSummary}\nCode:\n";
        header = Bound(header, maxChars);
        return header + Bound(BodyExcerpt, Math.Min(Math.Clamp(maxBodyChars, 0, 12000), maxChars - header.Length));
    }

    /// <summary>Deterministic head/tail excerpt preserving UTF-16 scalar boundaries.</summary>
    public static string Bound(string text, int maxChars)
    {
        if (maxChars <= 0) return "";
        if (text.Length <= maxChars) return text;
        const string omission = "\n<omitted>\n";
        if (maxChars < omission.Length)
            return text[..SafeEnd(text, maxChars)];
        var remaining = maxChars - omission.Length;
        var head = SafeEnd(text, (remaining + 1) / 2);
        var tail = text.Length - remaining / 2;
        if (tail < text.Length && char.IsLowSurrogate(text[tail])) tail++;
        return text[..head] + omission + text[tail..];
    }

    private static int SafeEnd(string text, int end) =>
        end > 0 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])
            ? end - 1 : end;
}
