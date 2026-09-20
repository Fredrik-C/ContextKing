using ContextKing.Core.Embedding;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Embedding;

public class CodeEmbeddingSecurityTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public void SymlinkedModelAssetsAreRejected()
    {
        var external = Path.Combine(Path.GetTempPath(), "ck-model-secret-" + Path.GetRandomFileName());
        File.WriteAllText(external, "model asset");
        try
        {
            foreach (var name in new[] { "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json", "LICENSE" })
                _repo.WriteFile(name, "model asset");
            var link = Path.Combine(_repo.Root, "LICENSE");
            File.Delete(link);
            try { File.CreateSymbolicLink(link, external); }
            catch (IOException) { return; } // Windows CI without symlink privilege.
            var files = Directory.GetFiles(_repo.Root).Select(p => (Name: Path.GetFileName(p)!, Path: p))
                .ToDictionary(x => x.Name, x => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(x.Path))), StringComparer.Ordinal);
            var manifest = new CodeModelManifest { ModelId = "x", ModelVersion = "1", License = "MIT", Dimensions = 768, MaxTokens = 512, Sha256 = files["model.onnx"], Files = files };
            _repo.WriteFile("manifest.json", System.Text.Json.JsonSerializer.Serialize(manifest));
            var load = () => CodeModelManifest.LoadAndVerify(_repo.Root);
            load.Should().Throw<InvalidDataException>().WithMessage("*regular files*");
        }
        finally { File.Delete(external); }
    }

    public void Dispose() => _repo.Dispose();
}
