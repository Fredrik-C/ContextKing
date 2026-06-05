namespace ContextKing.Cli;

internal static class CkConfig
{
    internal static bool IsBrainEnabled(string repoRoot)
        => CkSettings.Load(repoRoot).Brain;
}
