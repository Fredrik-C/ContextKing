using System.Security.Cryptography;
using System.Text.Json;
using ContextKing.Core.Embedding;
using ContextKing.Tests.Helpers;
using FluentAssertions;

namespace ContextKing.Tests.Embedding;

public class CodeModelManifestTests : IDisposable
{
    private readonly TempRepo _repo = new();

    [Fact]
    public void EveryAssetIsVerifiedBeforeInference()
    {
        var manifest = WritePack();
        CodeModelManifest.LoadAndVerify(_repo.Root).ModelId.Should().Be(manifest.ModelId);
        _repo.WriteFile("vocab.txt", "changed");
        var load = () => CodeModelManifest.LoadAndVerify(_repo.Root);
        load.Should().Throw<InvalidDataException>().WithMessage("*Checksum mismatch*");
    }

    [Fact]
    public void MissingLicenseAndPathTraversalAreRejected()
    {
        var manifest = WritePack();
        manifest.Files.Remove("LICENSE");
        Save(manifest);
        var load = () => CodeModelManifest.LoadAndVerify(_repo.Root);
        load.Should().Throw<InvalidDataException>();
        manifest = WritePack();
        manifest.Files["../outside"] = new string('0', 64);
        Save(manifest);
        load.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(0, 8192)]
    [InlineData(768, 100000)]
    public void InvalidDimensionsAndTokenCapsAreRejected(int dimensions, int tokens)
    {
        Save(WritePack() with { Dimensions = dimensions, MaxTokens = tokens });
        var load = () => CodeModelManifest.LoadAndVerify(_repo.Root);
        load.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void BatchAgreementToleranceMustBeBounded()
    {
        Save(WritePack() with { BatchAgreementMinCosine = 1.1 });
        var load = () => CodeModelManifest.LoadAndVerify(_repo.Root);
        load.Should().Throw<InvalidDataException>();
    }

    private CodeModelManifest WritePack()
    {
        var files = new Dictionary<string, string>();
        foreach (var asset in new[] { "model.onnx", "vocab.txt", "tokenizer_config.json", "config.json", "LICENSE" })
        {
            _repo.WriteFile(asset, "test asset");
            files[asset] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_repo.Root, asset))));
        }
        var manifest = new CodeModelManifest
        {
            ModelId = "test-model", ModelVersion = "1", License = "MIT", Dimensions = 768, MaxTokens = 8192,
            Sha256 = files["model.onnx"], Files = files
        };
        Save(manifest);
        return manifest;
    }
    private void Save(CodeModelManifest manifest) => _repo.WriteFile("manifest.json", JsonSerializer.Serialize(manifest));
    public void Dispose() => _repo.Dispose();
}
