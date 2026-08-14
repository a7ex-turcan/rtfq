using YamlDotNet.RepresentationModel;

namespace Rtfq.Server.Configuration;

/// <summary>
/// A cursor over a YAML mapping that records which keys were read, so anything
/// left over can be reported as an unknown key with a line number.
///
/// That last part is the point: a typo in a config that decides who may write to
/// production should be a loud error at load, not a silently-defaulted value.
/// </summary>
internal sealed class MapCursor(YamlMappingNode node, string path, List<Diagnostic> diagnostics)
{
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    bool _allUsed;

    public string Path => path;
    public int Line => (int)node.Start.Line;

    /// <summary>Reads a scalar, resolving <c>${env:}</c> / <c>${file:}</c> references.</summary>
    public string? Secret(string key, out bool wasReference)
    {
        wasReference = false;
        var raw = RawScalar(key);
        if (raw is null) return null;

        var resolved = SecretResolver.Resolve(raw);
        wasReference = resolved.WasReference;
        if (resolved.Error is not null)
        {
            diagnostics.Add(new("config.secret_unresolved", Severity.Error, resolved.Error, Join(key), LineOf(key)));
            return null;
        }
        return resolved.Value;
    }

    public string? RawScalar(string key)
    {
        _used.Add(key);
        return node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s
            ? s.Value
            : null;
    }

    public MapCursor? Map(string key)
    {
        _used.Add(key);
        return node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode m
            ? new MapCursor(m, Join(key), diagnostics)
            : null;
    }

    public MapCursor? Seq(string key)
    {
        _used.Add(key);
        return node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlSequenceNode
            ? new MapCursor(node, Join(key), diagnostics) { SequenceKey = key }
            : null;
    }

    string? SequenceKey { get; init; }

    /// <summary>Items of the sequence this cursor was opened on.</summary>
    public IEnumerable<(YamlNode Node, int Index)> Items()
    {
        if (SequenceKey is null) yield break;
        if (!node.Children.TryGetValue(new YamlScalarNode(SequenceKey), out var v) || v is not YamlSequenceNode seq)
            yield break;

        var i = 0;
        foreach (var item in seq) yield return (item, i++);
    }

    /// <summary>Items of a named sequence under this mapping.</summary>
    public IEnumerable<(YamlNode Node, int Index)> SeqItems(string key)
    {
        _used.Add(key);
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq)
            yield break;

        var i = 0;
        foreach (var item in seq) yield return (item, i++);
    }

    public IEnumerable<(string Key, string? Value)> Entries()
    {
        foreach (var (k, v) in node.Children)
        {
            if (k is not YamlScalarNode ks || ks.Value is null) continue;
            yield return (ks.Value, (v as YamlScalarNode)?.Value);
        }
    }

    public IReadOnlyList<string> StringList(string key)
    {
        _used.Add(key);
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v) || v is not YamlSequenceNode seq)
            return [];

        var list = new List<string>();
        foreach (var item in seq)
            if (item is YamlScalarNode s && s.Value is not null)
                list.Add(s.Value);
        return list;
    }

    public int Int(string key, int fallback, List<Diagnostic> diags)
        => NullableInt(key, diags) ?? fallback;

    public int? NullableInt(string key, List<Diagnostic> diags)
    {
        var raw = RawScalar(key);
        if (raw is null) return null;
        if (int.TryParse(raw, out var n) && n > 0) return n;

        diags.Add(new("config.not_a_positive_integer", Severity.Error,
            $"'{raw}' is not a positive integer", Join(key), LineOf(key)));
        return null;
    }

    public TimeSpan Duration(string key, TimeSpan fallback, List<Diagnostic> diags)
        => NullableDuration(key, diags) ?? fallback;

    public TimeSpan? NullableDuration(string key, List<Diagnostic> diags)
    {
        var raw = RawScalar(key);
        if (raw is null) return null;
        if (Configuration.Duration.TryParse(raw, out var value)) return value;

        diags.Add(new("config.not_a_duration", Severity.Error,
            $"'{raw}' is not a duration - use forms like 15s, 2m, 1h", Join(key), LineOf(key)));
        return null;
    }

    /// <summary>Marks keys as known without reading them, for fields later milestones own.</summary>
    public void Reserve(params string[] keys)
    {
        foreach (var k in keys) _used.Add(k);
    }

    public void MarkAllUsed() => _allUsed = true;

    public void ReportUnknownKeys()
    {
        if (_allUsed) return;
        foreach (var (k, _) in node.Children)
        {
            if (k is not YamlScalarNode ks || ks.Value is null) continue;
            if (_used.Contains(ks.Value)) continue;

            diagnostics.Add(new("config.unknown_key", Severity.Error,
                $"unknown key '{ks.Value}'", Join(ks.Value), (int)ks.Start.Line));
        }
    }

    string Join(string key) => path.Length == 0 ? key : $"{path}.{key}";

    int LineOf(string key) =>
        (int)(node.Children.TryGetValue(new YamlScalarNode(key), out var v) ? v.Start.Line : node.Start.Line);
}
