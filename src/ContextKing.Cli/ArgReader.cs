using System.Globalization;

namespace ContextKing.Cli;

/// <summary>
/// Minimal argument reader shared by every <c>ck</c> subcommand.
/// Single responsibility: pull <c>--flag</c>, <c>--flag value</c>, and positional arguments
/// out of a <c>string[]</c> in the most obvious way, without any framework dependency.
/// Unknown flags silently pass through so individual commands can layer their own parsing
/// on top where needed. When a required value is missing, the return value is false and
/// the caller decides how to report the error (keeping output formatting inside the command).
/// </summary>
internal sealed class ArgReader
{
    private readonly string[] _args;
    private readonly bool[]   _consumed;

    public ArgReader(string[] args)
    {
        _args     = args;
        _consumed = new bool[args.Length];
    }

    /// <summary><c>true</c> when the argument list is empty.</summary>
    public bool IsEmpty => _args.Length == 0;

    /// <summary><c>true</c> when the first argument is <c>--help</c> or <c>-h</c>.</summary>
    public bool IsHelp =>
        _args.Length > 0 && (_args[0] == "--help" || _args[0] == "-h");

    /// <summary><c>true</c> when any argument matches <paramref name="flag"/> or any of <paramref name="aliases"/>.</summary>
    public bool HasFlag(string flag, params string[] aliases)
    {
        for (int i = 0; i < _args.Length; i++)
        {
            if (_consumed[i]) continue;
            if (_args[i] == flag || Array.IndexOf(aliases, _args[i]) >= 0)
            {
                _consumed[i] = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the value following the first occurrence of <paramref name="flag"/>
    /// (or any alias), or <c>null</c> when the flag is absent.
    /// </summary>
    public string? GetString(string flag, params string[] aliases)
    {
        for (int i = 0; i < _args.Length - 1; i++)
        {
            if (_consumed[i]) continue;
            if (_args[i] == flag || Array.IndexOf(aliases, _args[i]) >= 0)
            {
                _consumed[i]     = true;
                _consumed[i + 1] = true;
                return _args[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Returns every value that follows an occurrence of <paramref name="flag"/>,
    /// supporting repeatable flags such as <c>--must</c>.
    /// </summary>
    public List<string> GetStringList(string flag, params string[] aliases)
    {
        var result = new List<string>();
        for (int i = 0; i < _args.Length - 1; i++)
        {
            if (_consumed[i]) continue;
            if (_args[i] == flag || Array.IndexOf(aliases, _args[i]) >= 0)
            {
                _consumed[i]     = true;
                _consumed[i + 1] = true;
                result.Add(_args[i + 1]);
            }
        }
        return result;
    }

    /// <summary>
    /// Parses the value following <paramref name="flag"/> as an <see cref="int"/>.
    /// Returns <c>true</c> when the flag is present AND parseable. <paramref name="value"/>
    /// is set to the parsed number or <c>default</c>.
    /// </summary>
    public bool TryGetInt(string flag, out int value, params string[] aliases)
    {
        var raw = GetString(flag, aliases);
        if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        value = default;
        return false;
    }

    /// <summary>
    /// Parses the value following <paramref name="flag"/> as a <see cref="float"/> using invariant culture.
    /// </summary>
    public bool TryGetFloat(string flag, out float value, params string[] aliases)
    {
        var raw = GetString(flag, aliases);
        if (raw is not null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        value = default;
        return false;
    }

    /// <summary>
    /// Returns every remaining argument that is not a flag (does not start with <c>-</c>)
    /// and was not consumed by a prior call.
    /// </summary>
    public List<string> RemainingPositionals()
    {
        var result = new List<string>();
        for (int i = 0; i < _args.Length; i++)
        {
            if (_consumed[i]) continue;
            if (_args[i].StartsWith('-')) continue;
            result.Add(_args[i]);
        }
        return result;
    }
}
