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
    int MaxOverfetch = 200,
    float LexicalWeight = 0.65f,
    float SemanticWeight = 0.30f,
    float MustWeight = 0.10f,
    float GenericPenaltyMax = 0.10f)
{
    public static FindFilesSettings FromJson(JsonElement element)
    {
        var defaults = new FindFilesSettings();
        var minOverfetch = Math.Max(1, ReadInt(element, "minOverfetch") ?? defaults.MinOverfetch);
        var maxOverfetch = Math.Max(minOverfetch, ReadInt(element, "maxOverfetch") ?? defaults.MaxOverfetch);

        return new FindFilesSettings(
            ReadBool(element, "semanticRerank") ?? defaults.SemanticRerank,
            Math.Clamp(ReadInt(element, "overfetchMultiplier") ?? defaults.OverfetchMultiplier, 1, 50),
            minOverfetch,
            maxOverfetch,
            ClampWeight(ReadFloat(element, "lexicalWeight") ?? defaults.LexicalWeight, defaults.LexicalWeight),
            ClampWeight(ReadFloat(element, "semanticWeight") ?? defaults.SemanticWeight, defaults.SemanticWeight),
            ClampWeight(ReadFloat(element, "mustWeight") ?? defaults.MustWeight, defaults.MustWeight),
            ClampWeight(ReadFloat(element, "genericPenaltyMax") ?? defaults.GenericPenaltyMax, defaults.GenericPenaltyMax));
    }

    public int OverfetchTopK(int top) =>
        SemanticRerank
            ? Math.Clamp(top * OverfetchMultiplier, MinOverfetch, MaxOverfetch)
            : top;

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
        element.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static float? ReadFloat(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.TryGetSingle(out var value)
            ? value
            : null;

    private static float ClampWeight(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
}
