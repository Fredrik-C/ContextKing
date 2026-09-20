using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace ContextKing.Core.Embedding;

/// <summary>Offline CPU inference for verified BERT-tokenized retrieval model packs.</summary>
public sealed class CodeEmbeddingModel : IBatchTextEmbedder, IQueryTextEmbedder, IDisposable
{
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly object _gate = new();
    public CodeModelManifest Manifest { get; }

    public CodeEmbeddingModel(string directory)
    {
        Manifest = CodeModelManifest.LoadAndVerify(directory);
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "tokenizer_config.json")));
        var root = config.RootElement;
        if (!root.TryGetProperty("do_lower_case", out var lower) || lower.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Model pack requires the supported uncased BERT tokenizer.");
        _tokenizer = BertTokenizer.Create(Path.Combine(directory, "vocab.txt"), new BertOptions
        {
            LowerCaseBeforeTokenization = true,
            RemoveNonSpacingMarks = true,
            IndividuallyTokenizeCjk = true
        });
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
        };
        _session = new InferenceSession(Path.Combine(directory, "model.onnx"), options);
        if (!_session.OutputMetadata.ContainsKey(Manifest.OutputName)
            || !_session.InputMetadata.ContainsKey("input_ids") || !_session.InputMetadata.ContainsKey("attention_mask")
            || _session.InputMetadata.Keys.Any(n => n is not ("input_ids" or "attention_mask" or "token_type_ids")))
        {
            _session.Dispose();
            throw new InvalidDataException("Model tensor contract does not match the manifest.");
        }
    }

    public float[] Embed(string text) => EmbedBatch([text])[0];
    public float[] EmbedQuery(string text) => EmbedCore([Manifest.QueryPrefix + text])[0];
    public IReadOnlyList<float[]> EmbedBatch(IReadOnlyList<string> texts) =>
        EmbedCore(texts.Select(t => Manifest.DocumentPrefix + t).ToArray());

    private IReadOnlyList<float[]> EmbedCore(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        lock (_gate)
        {
            var sequences = texts.Select(t => _tokenizer.EncodeToIds(t, Manifest.MaxTokens, out _, out _).ToArray()).ToArray();
            var results = new List<float[]>(texts.Count);
            // Dynamic padding and a small token budget bound temporary tensors.
            for (var start = 0; start < sequences.Length;)
            {
                var count = 1;
                var length = sequences[start].Length;
                while (count < 8 && start + count < sequences.Length
                    && Math.Max(length, sequences[start + count].Length) * (count + 1) <= 4096)
                {
                    length = Math.Max(length, sequences[start + count].Length); count++;
                }
                var ids = new DenseTensor<long>([count, length]);
                var mask = new DenseTensor<long>([count, length]);
                for (var row = 0; row < count; row++)
                    for (var column = 0; column < sequences[start + row].Length; column++)
                    {
                        ids[row, column] = sequences[start + row][column]; mask[row, column] = 1;
                    }
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", ids),
                    NamedOnnxValue.CreateFromTensor("attention_mask", mask)
                };
                if (_session.InputMetadata.ContainsKey("token_type_ids"))
                    inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>([count, length])));
                using var output = _session.Run(inputs, [Manifest.OutputName]);
                var tensor = output.First().AsTensor<float>();
                var pooled = Manifest.Pooling == "pooled";
                if (tensor.Rank != (pooled ? 2 : 3) || tensor.Dimensions[0] != count
                    || tensor.Dimensions[^1] != Manifest.Dimensions || (!pooled && tensor.Dimensions[1] != length))
                    throw new InvalidDataException("Model output dimensions differ from the manifest.");
                for (var row = 0; row < count; row++)
                {
                    var vector = new float[Manifest.Dimensions];
                    for (var d = 0; d < vector.Length; d++)
                    {
                        if (pooled) vector[d] = tensor[row, d];
                        else if (Manifest.Pooling == "cls") vector[d] = tensor[row, 0, d];
                        else
                        {
                            double sum = 0;
                            for (var token = 0; token < sequences[start + row].Length; token++) sum += tensor[row, token, d];
                            vector[d] = (float)(sum / sequences[start + row].Length);
                        }
                    }
                    Normalize(vector);
                    results.Add(vector);
                }
                start += count;
            }
            return results;
        }
    }

    internal static void Normalize(float[] vector)
    {
        double squared = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite model vector.");
            squared += (double)value * value;
        }
        if (squared <= 0) throw new InvalidDataException("Empty model vector.");
        var norm = Math.Sqrt(squared);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    public void Dispose() { lock (_gate) _session.Dispose(); }
}
