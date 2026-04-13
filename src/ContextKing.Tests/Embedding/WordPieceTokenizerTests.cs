using ContextKing.Core.Embedding;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Embedding;

public class WordPieceTokenizerTests
{
    private readonly WordPieceTokenizer _tokenizer = new(TestEmbedder.VocabPath);

    [Fact]
    public void Tokenize_OutputArraysHaveRequestedLength()
    {
        var (inputIds, mask, typeIds) = _tokenizer.Tokenize("hello", maxLength: 16);

        inputIds.Should().HaveCount(16);
        mask.Should().HaveCount(16);
        typeIds.Should().HaveCount(16);
    }

    [Fact]
    public void Tokenize_FirstTokenIsCLS()
    {
        var (inputIds, _, _) = _tokenizer.Tokenize("hello");
        inputIds[0].Should().Be(101); // [CLS]
    }

    [Fact]
    public void Tokenize_SEPTokenPresentAfterContent()
    {
        var (inputIds, mask, _) = _tokenizer.Tokenize("hello", maxLength: 16);
        // Find where attention mask goes to zero — SEP is the last real token
        int lastReal = Array.LastIndexOf(mask, 1L);
        inputIds[lastReal].Should().Be(102); // [SEP]
    }

    [Fact]
    public void Tokenize_PaddingPositionsHaveZeroAttentionMask()
    {
        var (inputIds, mask, _) = _tokenizer.Tokenize("hello", maxLength: 32);
        // After [CLS] "hello" [SEP] there should be padding with mask=0
        mask.Should().Contain(0L);
        // All padding input IDs should be 0
        for (int i = 0; i < 32; i++)
            if (mask[i] == 0L)
                inputIds[i].Should().Be(0L);
    }

    [Fact]
    public void Tokenize_TokenTypeIdsAllZero()
    {
        var (_, _, typeIds) = _tokenizer.Tokenize("stripe payment");
        typeIds.Should().AllBeEquivalentTo(0L);
    }

    [Fact]
    public void Tokenize_TruncatesLongInputToMaxLength()
    {
        // Generate a very long text
        var longText = string.Join(" ", Enumerable.Repeat("payment", 200));
        var (inputIds, mask, _) = _tokenizer.Tokenize(longText, maxLength: 32);

        inputIds.Should().HaveCount(32);
        // Last real token must be [SEP]
        int lastReal = Array.LastIndexOf(mask, 1L);
        inputIds[lastReal].Should().Be(102);
    }

    [Fact]
    public void Tokenize_KnownWordProducesNonUnkTokenId()
    {
        // "stripe" is unlikely to be in BERT vocab directly, but "payment" often is
        // We verify at minimum that the sequence contains tokens other than [UNK]=100
        var (inputIds, mask, _) = _tokenizer.Tokenize("the", maxLength: 16);
        // "the" is always in BERT vocab — should not produce [UNK]
        var realTokens = inputIds.Zip(mask).Where(t => t.Second == 1L).Select(t => t.First).ToList();
        realTokens.Should().NotContain(100L); // 100 = [UNK]
    }

    [Fact]
    public void Tokenize_SameInputProducesSameOutput()
    {
        var (ids1, _, _) = _tokenizer.Tokenize("reconciliation");
        var (ids2, _, _) = _tokenizer.Tokenize("reconciliation");
        ids1.Should().Equal(ids2);
    }
}
