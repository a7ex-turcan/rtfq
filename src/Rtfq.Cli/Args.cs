namespace Rtfq.Cli;

/// <summary>
/// A deliberately small argument parser.
///
/// M0 has four commands and a handful of flags. A dependency for that would cost
/// more than it saves, and the alternative in this ecosystem is currently a beta
/// whose API is still moving. Revisit if the surface grows past what fits here.
/// </summary>
internal sealed class Args
{
    readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    readonly List<string> _positional = [];
    readonly List<string> _unknown = [];

    public string Command { get; }
    public IReadOnlyList<string> Positional => _positional;
    public IReadOnlyList<string> UnknownOptions => _unknown;

    /// <param name="flags">Options that take no value, so "--production" is not read as "--production &lt;next&gt;".</param>
    public Args(string[] argv, IReadOnlyCollection<string> flags)
    {
        Command = argv.Length > 0 && !argv[0].StartsWith('-') ? argv[0] : "";

        for (var i = Command.Length > 0 ? 1 : 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                _positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;

            var eq = name.IndexOf('=');
            if (eq > 0)
            {
                value = name[(eq + 1)..];
                name = name[..eq];
            }
            else if (!flags.Contains(name) && i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = argv[++i];
            }

            _options[name] = value;
        }
    }

    public string? Value(string name)
    {
        var found = _options.TryGetValue(name, out var value);
        if (found) _options.Remove(name);
        return found ? value : null;
    }

    public bool Has(string name)
    {
        var found = _options.ContainsKey(name);
        if (found) _options.Remove(name);
        return found;
    }

    /// <summary>
    /// Options nobody consumed. Reported rather than ignored: a mistyped flag that
    /// silently does nothing is how people believe a limit is set when it is not.
    /// </summary>
    public IReadOnlyList<string> Leftovers()
    {
        _unknown.AddRange(_options.Keys.Select(k => "--" + k));
        return _unknown;
    }
}
