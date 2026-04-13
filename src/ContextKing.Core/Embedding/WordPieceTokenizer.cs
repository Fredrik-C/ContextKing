namespace ContextKing.Core.Embedding;

/// <summary>
/// Minimal BERT WordPiece tokenizer. Loads vocab.txt and converts text to token IDs
/// suitable for use with the BGE-small-en-v1.5 ONNX model.
/// </summary>
public sealed class WordPieceTokenizer
{
    private const int ClsTokenId = 101;
    private const int SepTokenId = 102;
    private const int PadTokenId = 0;
    private const int UnkTokenId = 100;

    private readonly Dictionary<string, int> _vocab;

    public WordPieceTokenizer(string vocabPath)
    {
        var lines = File.ReadAllLines(vocabPath);
        _vocab = new Dictionary<string, int>(lines.Length, StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            var token = lines[i];
            if (!string.IsNullOrEmpty(token))
                _vocab[token] = i;
        }
    }

    /// <summary>
    /// Tokenises <paramref name="text"/> into (inputIds, attentionMask, tokenTypeIds)
    /// arrays of length <paramref name="maxLength"/>, ready for ONNX inference.
    /// </summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Tokenize(
        string text, int maxLength = 128)
    {
        var tokenIds = new List<int>(maxLength) { ClsTokenId };

        foreach (var word in text.ToLowerInvariant()
                     .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = WordPieceTokenize(word);
            if (tokenIds.Count + pieces.Count > maxLength - 1)
                break; // leave room for [SEP]
            tokenIds.AddRange(pieces);
        }

        tokenIds.Add(SepTokenId);

        var inputIds     = new long[maxLength];
        var attentionMask = new long[maxLength];
        var tokenTypeIds  = new long[maxLength];

        for (int i = 0; i < tokenIds.Count && i < maxLength; i++)
        {
            inputIds[i]      = tokenIds[i];
            attentionMask[i] = 1L;
            // tokenTypeIds stays 0 (single-sequence encoding)
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private List<int> WordPieceTokenize(string word)
    {
        if (_vocab.TryGetValue(word, out int directId))
            return [directId];

        var result = new List<int>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            bool found = false;

            while (start < end)
            {
                string sub = start == 0
                    ? word[start..end]
                    : "##" + word[start..end];

                if (_vocab.TryGetValue(sub, out int subId))
                {
                    result.Add(subId);
                    start = end;
                    found = true;
                    break;
                }

                end--;
            }

            if (!found)
                return [UnkTokenId];
        }

        return result;
    }
}
