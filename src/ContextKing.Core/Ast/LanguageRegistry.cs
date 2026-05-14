namespace ContextKing.Core.Ast;

public static class LanguageRegistry
{
    private static readonly Dictionary<string, ILanguageExtractor> Extractors = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    public static void Register(ILanguageExtractor extractor, params string[] extensions)
    {
        foreach (var ext in extensions)
        {
            var key = ext.StartsWith('.') ? ext : "." + ext;
            Extractors[key] = extractor;
        }
    }

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        Register(new CSharpRoslynExtractor(), ".cs");
        Register(new TypeScriptExtractor("typescript"), ".ts");
        Register(new TypeScriptExtractor("tsx"), ".tsx");
        Register(new KotlinExtractor(), ".kt", ".kts");
        Register(new PythonExtractor(), ".py");
        _initialized = true;
    }

    /// <summary>Attempts to find a configured extractor for the given file path using its extension.</summary>
    public static ILanguageExtractor? Get(string filePath)
    {
        EnsureInitialized();
        var ext = Path.GetExtension(filePath.AsSpan());
        foreach (var (key, extractor) in Extractors)
        {
            if (ext.Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return extractor;
        }
        return null;
    }

    /// <summary>Returns true when the given file path maps to a registered extractor.</summary>
    public static bool IsSupported(string filePath) => Get(filePath) is not null;

    /// <summary>The set of all file extensions currently registered, sorted for stable output.</summary>
    public static IReadOnlyList<string> RegisteredExtensions
    {
        get
        {
            EnsureInitialized();
            return Extractors.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Returns a git pathspec string suitable for 'git ls-files'.</summary>
    public static string BuildGitPathspec()
    {
        return string.Join(" ", RegisteredExtensions.Select(ext => "-- *" + ext));
    }
}
