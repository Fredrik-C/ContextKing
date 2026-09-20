using ContextKing.Core.Embedding;

namespace ContextKing.Cli;

/// <summary>Reuses one optional code-model session per process. Never downloads during search.</summary>
internal static class CodeModelLocator
{
    private static readonly object Gate = new();
    private static string? _loadedPath;
    private static CodeEmbeddingModel? _model;

    internal static ITextEmbedder GetEmbedder(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || modelName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Invalid methodRerankModel name.");
        var path = Environment.GetEnvironmentVariable("CK_CODE_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(path))
        {
            var besideInstall = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "models", modelName));
            path = Directory.Exists(besideInstall) ? besideInstall
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ck", "models", modelName);
        }
        path = Path.GetFullPath(path);
        lock (Gate)
        {
            if (_loadedPath == path && _model is not null) return _model;
            if (!File.Exists(Path.Combine(path, "manifest.json")))
                throw new FileNotFoundException("Code model pack is missing. Re-run the Context King installer.");
            var replacement = new CodeEmbeddingModel(path);
            _model?.Dispose();
            _model = replacement;
            _loadedPath = path;
            return replacement;
        }
    }
}
