using Rtfq.Adapters;
using Rtfq.Adapters.Postgres;
using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server;

/// <summary>
/// Builds and owns one adapter per declared source.
///
/// This is the only place that maps a config <c>kind</c> onto a concrete adapter.
/// Adding an engine in M2 means adding a case here and a class under
/// <c>Rtfq.Adapters</c> — nothing above this type learns a new name.
/// </summary>
public sealed class SourceRegistry : IAsyncDisposable
{
    readonly Dictionary<string, ISourceAdapter> _adapters = new(StringComparer.Ordinal);

    public SourceRegistry(RtfqConfig config)
    {
        foreach (var source in config.Sources)
        {
            _adapters[source.Name] = source.Kind switch
            {
                "postgres" => new PostgresAdapter(
                    source.Name,
                    source.Dsn,
                    source.Schemas,
                    source.EffectiveStatementTimeout(config.Defaults)),

                _ => throw new AdapterException(ErrorCodes.ConfigInvalid,
                    $"source '{source.Name}' has unsupported kind '{source.Kind}'"),
            };
        }
    }

    public ISourceAdapter this[string name] => _adapters[name];

    public bool TryGet(string name, out ISourceAdapter adapter) => _adapters.TryGetValue(name, out adapter!);

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _adapters.Values)
            await adapter.DisposeAsync().ConfigureAwait(false);
        _adapters.Clear();
    }
}
