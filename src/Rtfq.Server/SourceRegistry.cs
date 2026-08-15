using Rtfq.Adapters;
using Rtfq.Adapters.Http;
using Rtfq.Adapters.Mongo;
using Rtfq.Adapters.Postgres;
using Rtfq.Adapters.SqlServer;
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

                "mssql" => new SqlServerAdapter(
                    source.Name,
                    source.Dsn,
                    source.Schemas,
                    source.EffectiveStatementTimeout(config.Defaults)),

                "mongodb" => new MongoAdapter(
                    source.Name,
                    source.Dsn,
                    source.Databases,
                    source.EffectiveStatementTimeout(config.Defaults)),

                "http" => new HttpAdapter(
                    source.Name,
                    source.BaseUrl,
                    source.Methods,
                    source.AllowPaths,
                    source.Headers,
                    source.EffectiveStatementTimeout(config.Defaults)),

                _ => throw new AdapterException(ErrorCodes.ConfigInvalid,
                    $"source '{source.Name}' has unsupported kind '{source.Kind}'"),
            };
        }
    }

    public ISourceAdapter this[string name] => _adapters[name];

    public bool TryGet(string name, out ISourceAdapter adapter) => _adapters.TryGetValue(name, out adapter!);

    public IEnumerable<ISourceAdapter> All => _adapters.Values;

    /// <param name="Fatal">
    /// True when the source answered and revealed it cannot support the access it
    /// declares. Distinct from merely being unreachable, which is not fatal.
    /// </param>
    public sealed record CapabilityProblem(string Source, string Message, bool Fatal);

    /// <summary>
    /// Checks each source's declared access against what it can actually do.
    ///
    /// Some capabilities are only knowable by connecting — MongoDB supports
    /// transactions on a replica set and not on a standalone, and no amount of
    /// reading YAML reveals which is out there. That splits validation in two:
    /// <c>rtfq validate</c> stays offline and static, and this runs at startup.
    ///
    /// An unreachable source is <b>not</b> fatal. Refusing to start because one
    /// database is down would contradict the whole offline-discovery posture; the
    /// check simply has not happened yet, and it happens again on first use.
    /// </summary>
    public async Task<IReadOnlyList<CapabilityProblem>> CheckCapabilitiesAsync(
        RtfqConfig config, TimeSpan perSourceTimeout, CancellationToken cancellationToken)
    {
        var problems = new List<CapabilityProblem>();

        foreach (var source in config.Sources)
        {
            if (!_adapters.TryGetValue(source.Name, out var adapter)) continue;

            SourceCapabilities capabilities;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(perSourceTimeout);
                capabilities = await adapter.CheckAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AdapterException or OperationCanceledException)
            {
                problems.Add(new CapabilityProblem(source.Name,
                    $"could not be reached at startup: {ex.Message}. Discovery will serve any cached schema; " +
                    "capabilities are re-checked on first use.", Fatal: false));
                continue;
            }

            if (source.Access >= AccessLevel.Write && !capabilities.TransactionalWrites)
            {
                problems.Add(new CapabilityProblem(source.Name,
                    $"declares access: {source.Access.ToWire()} but its deployment cannot do transactional writes. " +
                    "For MongoDB this means a standalone server: writes require a replica set.", Fatal: true));
            }

            if (source.Access >= AccessLevel.Schema && !capabilities.TransactionalDdl)
            {
                problems.Add(new CapabilityProblem(source.Name,
                    $"declares access: schema but cannot roll back DDL, so a failed schema change " +
                    "could not be undone (ADR 0002).", Fatal: true));
            }
        }

        return problems;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var adapter in _adapters.Values)
            await adapter.DisposeAsync().ConfigureAwait(false);
        _adapters.Clear();
    }
}
