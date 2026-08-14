using System.Text.Json;

namespace Rtfq.Server.Audit;

/// <param name="Classification">read / mutation / schema / refused. M0 only produces read and refused.</param>
/// <param name="Outcome">ok or error.</param>
public sealed record AuditEntry
{
    public required string RequestId { get; init; }
    public required string Operation { get; init; }
    public string? TokenId { get; init; }
    public string? Source { get; init; }
    public string? Statement { get; init; }
    public string Classification { get; init; } = "unknown";
    public required string Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public int? RowCount { get; init; }
    public bool? Truncated { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// Append-only JSONL on the local box. Never shipped anywhere — there is no
/// control plane and never will be (CLAUDE.md non-goal #5).
///
/// Writes are synchronous and flushed. An audit record that might not survive a
/// crash is not an audit record, and the volume here is a handful of lines per
/// query, so buffering would trade the only property that matters for a
/// throughput gain nobody asked for.
///
/// Serialization is hand-written with <see cref="Utf8JsonWriter"/> rather than a
/// serializer: no reflection, nothing for the trimmer to remove (ADR 0001).
/// </summary>
public sealed class AuditLog : IDisposable
{
    readonly Lock _gate = new();
    readonly FileStream _stream;

    public string Path { get; }

    public AuditLog(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        Path = System.IO.Path.Combine(stateDirectory, "audit.jsonl");
        _stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void Write(AuditEntry entry)
    {
        var buffer = new MemoryStream(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("ts", DateTimeOffset.UtcNow.ToString("O"));
            w.WriteString("request_id", entry.RequestId);
            w.WriteString("operation", entry.Operation);
            WriteIfPresent(w, "token_id", entry.TokenId);
            WriteIfPresent(w, "source", entry.Source);
            // M0 records the statement verbatim. Per-policy redaction arrives with
            // the write path, where statements start carrying values.
            WriteIfPresent(w, "statement", entry.Statement);
            w.WriteString("classification", entry.Classification);
            w.WriteString("outcome", entry.Outcome);
            WriteIfPresent(w, "error_code", entry.ErrorCode);
            if (entry.RowCount is { } rows) w.WriteNumber("row_count", rows);
            if (entry.Truncated is { } truncated) w.WriteBoolean("truncated", truncated);
            w.WriteNumber("elapsed_ms", entry.ElapsedMs);
            w.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);

        lock (_gate)
        {
            _stream.Write(bytes);
            _stream.Flush();
        }
    }

    static void WriteIfPresent(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteString(name, value);
    }

    public void Dispose()
    {
        lock (_gate) _stream.Dispose();
    }
}
