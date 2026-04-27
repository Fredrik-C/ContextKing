namespace ContextKing.Cli;

internal static class CkConfig
{
    internal static bool IsBrainEnabled(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".ck.json");
        if (!File.Exists(path)) return true;
        try
        {
            var json = File.ReadAllText(path);
            var m = System.Text.RegularExpressions.Regex.Match(
                json, @"""brain""\s*:\s*(true|false)");
            if (m.Success) return m.Groups[1].Value != "false";
        }
        catch { }
        return true;
    }
}
