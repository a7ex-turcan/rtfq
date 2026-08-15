using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Rtfq.Adapters;
using Rtfq.Contracts;

namespace Rtfq.Server.Schema;

/// <param name="Age">How long ago the snapshot was captured. Always reported, never inferred from silence.</param>
/// <param name="Stale">Whether <see cref="Age"/> exceeds the configured TTL.</param>
public sealed record CachedSchema(SchemaSnapshot Snapshot, TimeSpan Age, bool Stale);

[JsonSerializable(typeof(SchemaSnapshot))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class SchemaJson : JsonSerializerContext;

/// <summary>
/// The schema cache, and the reason <c>describe_*</c> keeps working when the
/// database does not.
///
/// Learning a table's shape must not require the source to be reachable at that
/// instant: an agent should be able to draft a correct statement offline and only
/// need the database live to run it. Per ADR 0003 a stale snapshot is served
/// immediately and refreshed behind the response; only a cold miss blocks.
/// </summary>
public sealed class SchemaCache(
    string stateDirectory,
    TimeSpan ttl,
    SourceRegistry sources,
    ILogger<SchemaCache> logger)
{
    readonly ConcurrentDictionary<string, SchemaSnapshot> _memory = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);
    readonly string _directory = Path.Combine(stateDirectory, "schema");

    public async Task<CachedSchema> GetAsync(string source, CancellationToken cancellationToken)
    {
        var snapshot = _memory.GetValueOrDefault(source) ?? LoadFromDisk(source);

        if (snapshot is not null)
        {
            _memory[source] = snapshot;
            var cached = Describe(snapshot);

            // Refresh behind the response rather than in front of it. An agent
            // calling describe_table several times while drafting a statement
            // should not wait on a catalog query each time.
            if (cached.Stale) StartBackgroundRefresh(source);
            return cached;
        }

        // Cold miss: nothing to be stale about, so this one blocks.
        return Describe(await IntrospectAndStoreAsync(source, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Forces a synchronous re-introspection, for an operator who has just run a migration.</summary>
    public async Task<CachedSchema> RefreshAsync(string source, CancellationToken cancellationToken) =>
        Describe(await IntrospectAndStoreAsync(source, cancellationToken).ConfigureAwait(false));

    CachedSchema Describe(SchemaSnapshot snapshot)
    {
        var age = DateTimeOffset.UtcNow - snapshot.CapturedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return new CachedSchema(snapshot, age, age > ttl);
    }

    async Task<SchemaSnapshot> IntrospectAndStoreAsync(string source, CancellationToken cancellationToken)
    {
        if (!sources.TryGet(source, out var adapter))
            throw new AdapterException(ErrorCodes.SourceUnknown, $"no source '{source}'");

        var snapshot = await adapter.IntrospectAsync(cancellationToken).ConfigureAwait(false);
        _memory[source] = snapshot;
        SaveToDisk(source, snapshot);
        return snapshot;
    }

    void StartBackgroundRefresh(string source)
    {
        // One refresh per source at a time: a stale entry plus concurrent agents
        // would otherwise stampede the catalog.
        if (!_refreshing.TryAdd(source, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await IntrospectAndStoreAsync(source, timeout.Token).ConfigureAwait(false);
                logger.LogInformation("Refreshed schema cache for {Source}", source);
            }
            catch (Exception ex)
            {
                // A background failure must never surface as a request failure.
                // The caller already sees the age climbing, which is the honest
                // signal; this is for whoever is reading the logs.
                logger.LogWarning(ex, "Background schema refresh failed for {Source}", source);
            }
            finally
            {
                _refreshing.TryRemove(source, out _);
            }
        });
    }

    // --- persistence --------------------------------------------------------

    string PathFor(string source) => Path.Combine(_directory, SafeFileName(source) + ".json");

    SchemaSnapshot? LoadFromDisk(string source)
    {
        var path = PathFor(source);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SchemaJson.Default.SchemaSnapshot);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            logger.LogWarning(ex, "Discarding unreadable schema cache for {Source}", source);
            return null;
        }
    }

    void SaveToDisk(string source, SchemaSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = PathFor(source);

            // Write beside the target and move into place. A half-written schema
            // file that survives a crash would be worse than no cache at all,
            // because it would be served confidently.
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot, SchemaJson.Default.SchemaSnapshot);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            // The cache is an optimisation plus an offline affordance, not a
            // correctness requirement. Failing to persist must not fail the call.
            logger.LogWarning(ex, "Could not persist schema cache for {Source}", source);
        }
    }

    static string SafeFileName(string source)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(source.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
