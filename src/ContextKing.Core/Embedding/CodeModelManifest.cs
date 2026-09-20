using System.Security.Cryptography;
using System.Text.Json;

namespace ContextKing.Core.Embedding;

/// <summary>Versioned local model contract. Every runtime asset is covered by a SHA-256 digest.</summary>
public sealed record CodeModelManifest
{
    public string ModelId { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public int Dimensions { get; init; }
    public int MaxTokens { get; init; }
    public string Pooling { get; init; } = "cls";
    public bool Normalized { get; init; } = true;
    public string Quantization { get; init; } = "int8";
    public string License { get; init; } = "";
    public string Tokenizer { get; init; } = "bert-uncased";
    public string QueryPrefix { get; init; } = "";
    public string DocumentPrefix { get; init; } = "";
    public string OutputName { get; init; } = "last_hidden_state";
    public double BatchAgreementMinCosine { get; init; } = 0.98;
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    public static CodeModelManifest LoadAndVerify(string directory)
    {
        var manifest = JsonSerializer.Deserialize<CodeModelManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty model manifest.");
        if (string.IsNullOrWhiteSpace(manifest.ModelId) || string.IsNullOrWhiteSpace(manifest.ModelVersion)
            || string.IsNullOrWhiteSpace(manifest.License) || manifest.Dimensions is < 1 or > 4096
            || manifest.MaxTokens is < 8 or > 8192 || manifest.Pooling is not ("cls" or "mean" or "pooled")
            || manifest.Tokenizer != "bert-uncased" || manifest.BatchAgreementMinCosine is < 0 or > 1 || manifest.Files is null)
            throw new InvalidDataException("Unsupported or invalid code model manifest.");
        foreach (var required in new[] { "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json", "LICENSE" })
            if (!manifest.Files.ContainsKey(required)) throw new InvalidDataException("Model manifest is missing a required asset checksum.");
        if (!string.Equals(manifest.Sha256, manifest.Files["model.onnx"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model checksum declarations disagree.");
        foreach (var (name, expected) in manifest.Files)
        {
            if (name != Path.GetFileName(name) || name.Contains('\\') || name.Contains('/') || name is "." or ".."
                || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid model asset name or checksum.");
            var assetPath = Path.GetFullPath(Path.Combine(directory, name));
            var modelRoot = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!assetPath.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(assetPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Model assets must be regular files inside the model directory.");
            using var stream = File.OpenRead(assetPath);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch for model asset {name}.");
        }
        return manifest;
    }
}
