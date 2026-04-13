using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ContextKing.Core.Embedding;

/// <summary>
/// Runs the BGE-small-en-v1.5 ONNX model to produce L2-normalised 384-dim embeddings.
/// The ONNX session is created lazily on first use and reused thereafter.
/// </summary>
public sealed class BgeEmbedder : IDisposable
{
    private const int HiddenSize = 384;

    private readonly WordPieceTokenizer _tokenizer;
    private readonly string _onnxPath;
    private InferenceSession? _session;
    private readonly object _lock = new();

    public BgeEmbedder(string modelDir)
    {
        var onnxFiles = Directory.GetFiles(modelDir, "*.onnx", SearchOption.AllDirectories);
        if (onnxFiles.Length == 0)
            throw new FileNotFoundException(
                $"No .onnx file found under '{modelDir}'. Run install script to populate the model directory.");

        // Prefer quantized model if available
        _onnxPath = Array.Find(onnxFiles, f => f.Contains("quantized"))
            ?? onnxFiles[0];

        var vocabPath = Path.Combine(modelDir, "vocab.txt");
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"vocab.txt not found at '{vocabPath}'.");

        _tokenizer = new WordPieceTokenizer(vocabPath);
    }

    /// <summary>
    /// Embeds <paramref name="text"/> into a 384-dim L2-normalised float vector.
    /// Thread-safe; the ONNX session is initialised on first call.
    /// </summary>
    public float[] Embed(string text)
    {
        var session = GetOrCreateSession();
        var (inputIds, attentionMask, tokenTypeIds) = _tokenizer.Tokenize(text);
        int seqLen = inputIds.Length;

        var inputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids",
                new DenseTensor<long>(inputIds, [1, seqLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask",
                new DenseTensor<long>(attentionMask, [1, seqLen])),
            NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(tokenTypeIds, [1, seqLen])),
        };

        using var outputs = session.Run(inputs);

        // BGE outputs last_hidden_state: [1, seq_len, hidden_size]
        var hiddenState = outputs
            .First(o => o.Name.Contains("last_hidden_state", StringComparison.OrdinalIgnoreCase))
            .AsTensor<float>();

        return MeanPoolAndNormalise(hiddenState, attentionMask, seqLen);
    }

    private static float[] MeanPoolAndNormalise(
        Tensor<float> hiddenState, long[] attentionMask, int seqLen)
    {
        var embedding = new float[HiddenSize];
        int count = 0;

        for (int i = 0; i < seqLen; i++)
        {
            if (attentionMask[i] == 0L) continue;
            count++;
            for (int j = 0; j < HiddenSize; j++)
                embedding[j] += hiddenState[0, i, j];
        }

        if (count > 0)
            for (int j = 0; j < HiddenSize; j++)
                embedding[j] /= count;

        // L2 normalise
        float norm = 0f;
        for (int j = 0; j < HiddenSize; j++)
            norm += embedding[j] * embedding[j];
        norm = MathF.Sqrt(norm);
        if (norm > 1e-9f)
            for (int j = 0; j < HiddenSize; j++)
                embedding[j] /= norm;

        return embedding;
    }

    private InferenceSession GetOrCreateSession()
    {
        if (_session is not null) return _session;
        lock (_lock)
        {
            if (_session is null)
            {
                var opts = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
                _session = new InferenceSession(_onnxPath, opts);
            }
        }
        return _session;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
