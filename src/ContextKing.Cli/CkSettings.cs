using System.Text.Json;
using ContextKing.Core.SourceMap;

namespace ContextKing.Cli;

internal sealed record CkSettings(
    string? MinVersion,
    bool Brain,
    FindFilesSettings FindFiles)
{
    public static CkSettings Load(string repoRoot, bool verbose = false)
    {
        var path = Path.Combine(repoRoot, ".ck.json");
        if (!File.Exists(path))
            return Defaults();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var minVersion = ReadString(root, "minVersion");
            var brain = ReadBool(root, "brain") ?? true;
            var findFiles = root.TryGetProperty("findFiles", out var findFilesElement)
                && findFilesElement.ValueKind == JsonValueKind.Object
                ? FindFilesSettings.FromJson(findFilesElement)
                : new FindFilesSettings();
            if (verbose && findFilesElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                Console.Error.WriteLine("[ck] WARN: findFiles must be an object. Using defaults.");

            return new CkSettings(minVersion, brain, findFiles);
        }
        catch (Exception ex)
        {
            if (verbose)
                Console.Error.WriteLine($"[ck] WARN: could not parse .ck.json: {ex.Message}. Using defaults.");
            return Defaults();
        }
    }

    private static CkSettings Defaults() => new(null, true, new FindFilesSettings());

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;
}

internal sealed record FindFilesSettings(
    bool SemanticRerank = true,
    int OverfetchMultiplier = 5,
    int MinOverfetch = 50,
    int MaxOverfetch = 100,
    float LexicalWeight = 0.65f,
    float SemanticWeight = 0.30f,
    float MustWeight = 0.10f,
    float GenericPenaltyMax = 0.10f,
    bool MethodRerank = true,
    string MethodRerankModel = "code-reranker",
    int MethodCandidateFiles = 50,
    int MaxMethodsPerFile = 24,
    int MaxMethodsTotal = 500,
    int MaxMethodCardChars = 6000,
    int MaxBodyChars = 3500,
    float MethodLexicalWeight = 0.45f,
    float MetadataSemanticWeight = 0.15f,
    float MethodSemanticWeight = 0.35f,
    float StructuralBoostMax = 0.08f,
    float FlatMethodThreshold = 0.03f)
{
    public static FindFilesSettings FromJson(JsonElement element)
    {
        var defaults = new FindFilesSettings();
        var minOverfetch = Math.Clamp(ReadInt(element, "minOverfetch") ?? defaults.MinOverfetch, 1, 1000);
        var methodRerank = ReadBool(element, "methodRerank") ?? defaults.MethodRerank;
        var maxOverfetch = Math.Clamp(ReadInt(element, "maxOverfetch") ?? (methodRerank ? 100 : 200), minOverfetch, 1000);

        return new FindFilesSettings(
            ReadBool(element, "semanticRerank") ?? defaults.SemanticRerank,
            Math.Clamp(ReadInt(element, "overfetchMultiplier") ?? defaults.OverfetchMultiplier, 1, 50),
            minOverfetch,
            maxOverfetch,
            ClampWeight(ReadFloat(element, "lexicalWeight") ?? defaults.LexicalWeight, defaults.LexicalWeight),
            ClampWeight(ReadFloat(element, "semanticWeight") ?? defaults.SemanticWeight, defaults.SemanticWeight),
            ClampWeight(ReadFloat(element, "mustWeight") ?? defaults.MustWeight, defaults.MustWeight),
            ClampWeight(ReadFloat(element, "genericPenaltyMax") ?? defaults.GenericPenaltyMax, defaults.GenericPenaltyMax),
            methodRerank,
            element.TryGetProperty("methodRerankModel", out var model) && model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString())
                ? model.GetString()! : defaults.MethodRerankModel,
            Math.Clamp(ReadInt(element, "methodCandidateFiles") ?? 50, 1, 100),
            Math.Clamp(ReadInt(element, "maxMethodsPerFile") ?? 24, 1, 100),
            Math.Clamp(ReadInt(element, "maxMethodsTotal") ?? 500, 1, 2000),
            Math.Clamp(ReadInt(element, "maxMethodCardChars") ?? 6000, 128, 16000),
            Math.Clamp(ReadInt(element, "maxBodyChars") ?? 3500, 0, 12000),
            ClampWeight(ReadFloat(element, "lexicalWeight") ?? 0.45f, 0.45f),
            ClampWeight(ReadFloat(element, "metadataSemanticWeight") ?? 0.15f, 0.15f),
            ClampWeight(ReadFloat(element, "methodSemanticWeight") ?? 0.35f, 0.35f),
            Math.Min(0.08f, ClampWeight(ReadFloat(element, "structuralBoostMax") ?? 0.08f, 0.08f)),
            ClampWeight(ReadFloat(element, "flatMethodThreshold") ?? 0.03f, 0.03f));
    }

    public int OverfetchTopK(int top) =>
        SemanticRerank || MethodRerank
            ? (int)Math.Max(top, Math.Clamp((long)top * OverfetchMultiplier, MinOverfetch, MaxOverfetch))
            : top;

    public MethodExtractionOptions ToMethodExtractionOptions() =>
        new(MethodCandidateFiles, MaxMethodsPerFile, MaxMethodsTotal, MaxMethodCardChars, MaxBodyChars);

    public MethodFusionOptions ToMethodFusionOptions() =>
        new(MethodLexicalWeight, MetadataSemanticWeight, MethodSemanticWeight, StructuralBoostMax, GenericPenaltyMax);

    public SemanticRerankOptions ToSemanticOptions() =>
        new(
            LexicalWeight,
            SemanticWeight,
            MustWeight,
            GenericPenaltyMax);

    private static bool? ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            ? property.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static float? ReadFloat(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetSingle(out var value)
            ? value
            : null;

    private static float ClampWeight(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
}
