namespace Rtfq.Adapters;

/// <summary>
/// A source's schema, normalized away from any one engine's catalog.
///
/// This is what the cache stores and what <c>describe_*</c> renders, so it must
/// stay source-agnostic: if M2 has to widen it to fit SQL Server or Mongo, that
/// is a signal the shape is wrong, not that the model needs another field.
/// </summary>
public sealed record SchemaSnapshot
{
    public required string Source { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required List<TableSchema> Tables { get; init; }

    /// <summary>
    /// Whether the shape was inferred from sampled data rather than read from a
    /// catalog. False for SQL engines; true for MongoDB in M2, where honesty
    /// about inference matters more than the schema itself.
    /// </summary>
    public bool Inferred { get; init; }

    public TableSchema? Find(string qualifiedName) =>
        Tables.FirstOrDefault(t => string.Equals(t.QualifiedName, qualifiedName, StringComparison.Ordinal))
        // Bare names resolve only when unambiguous. Guessing across schemas is how
        // a caller ends up reading a table it did not mean.
        ?? Tables.SingleOrDefault(t => string.Equals(t.Name, qualifiedName, StringComparison.Ordinal));
}

public sealed record TableSchema
{
    public required string Schema { get; init; }
    public required string Name { get; init; }

    /// <summary>table, view, matview or foreign.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Planner estimate, not a count. Deliberately not <c>COUNT(*)</c>: an
    /// introspection pass that table-scans every relation is a denial of service
    /// against the database we were asked to be careful with.
    /// </summary>
    public long? EstimatedRows { get; init; }

    public required List<ColumnSchema> Columns { get; init; }
    public List<string> PrimaryKey { get; init; } = [];
    public List<IndexSchema> Indexes { get; init; } = [];
    public List<ForeignKeySchema> ForeignKeys { get; init; } = [];

    public string QualifiedName => $"{Schema}.{Name}";
}

public sealed record ColumnSchema
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required bool Nullable { get; init; }
    public string? Default { get; init; }
}

public sealed record IndexSchema
{
    public required string Name { get; init; }
    public required List<string> Columns { get; init; }
    public required bool Unique { get; init; }
    public bool Primary { get; init; }
}

public sealed record ForeignKeySchema
{
    public required List<string> Columns { get; init; }
    public required string ReferencedTable { get; init; }
    public required List<string> ReferencedColumns { get; init; }
}
