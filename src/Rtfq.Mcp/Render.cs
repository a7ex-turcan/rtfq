using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rtfq.Contracts;

namespace Rtfq.Mcp;

/// <summary>
/// Renders API responses as dense text.
///
/// This is the part of M1 that decides whether the tool surface is usable. An
/// agent pays for every token of every tool result, on every call, for the whole
/// conversation — so the same information as JSON, with its quoting and repeated
/// key names, can cost several times as much and teach no more. The rule here is
/// one fact per line, no padding, no punctuation that carries no meaning.
/// </summary>
public static class Render
{
    public static string Sources(SourcesResponse response)
    {
        if (response.Sources.Count == 0)
            return "No sources are available to this token.";

        var sb = new StringBuilder();
        foreach (var s in response.Sources)
        {
            sb.Append(s.Name).Append(' ').Append('(').Append(s.Kind).Append(") ").Append(s.EffectiveAccess);
            if (s.Description.Length > 0) sb.Append(" - ").Append(s.Description);
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string Source(DescribeSourceResponse response)
    {
        var sb = new StringBuilder();
        sb.Append(response.Source).Append(" (").Append(response.Kind).Append(") ")
          .AppendLine(response.EffectiveAccess);

        if (response.Description.Length > 0) sb.AppendLine(response.Description);
        sb.AppendLine(Freshness(response.Schema));

        if (response.Tables.Count == 0)
        {
            sb.AppendLine(response.TableCount == 0 ? "no tables" : "no tables matched");
        }
        else
        {
            sb.Append(response.TableCount).AppendLine(" table(s):");

            // Grouped by schema so the qualified prefix is not repeated on every
            // line, which on a 200-table database is most of the output.
            foreach (var group in response.Tables.GroupBy(t => Schema(t.Name)))
            {
                sb.Append("[").Append(group.Key).AppendLine("]");
                foreach (var table in group)
                {
                    sb.Append("  ").Append(Bare(table.Name));
                    if (table.EstimatedRows is { } rows) sb.Append(" ~").Append(Count(rows));
                    sb.Append(' ').Append(table.Columns).Append("c");
                    if (table.Kind != "table") sb.Append(' ').Append(table.Kind);
                    sb.AppendLine();
                }
            }
        }

        if (response.Hint is { } hint) sb.AppendLine(hint);
        return sb.ToString().TrimEnd();
    }

    public static string Table(DescribeTableResponse response)
    {
        var sb = new StringBuilder();
        sb.Append(response.Table).Append(' ').Append(response.Kind);
        if (response.EstimatedRows is { } rows) sb.Append(" ~").Append(Count(rows)).Append(" rows");
        sb.AppendLine(response.Writable ? " writable" : " read-only");
        sb.AppendLine(Freshness(response.Schema));

        var primaryKey = new HashSet<string>(response.PrimaryKey, StringComparer.Ordinal);

        sb.AppendLine("columns:");
        foreach (var c in response.Columns)
        {
            sb.Append("  ").Append(c.Name).Append(' ').Append(ShortType(c.Type));
            if (!c.Nullable) sb.Append(" not-null");
            if (primaryKey.Contains(c.Name)) sb.Append(" pk");
            if (c.Default is { } d) sb.Append(' ').Append(ShortDefault(d));
            sb.AppendLine();
        }

        // Indexes matter to an agent writing a WHERE clause, so they are worth
        // their tokens; the primary-key index is already implied above.
        var indexes = response.Indexes.Where(i => !i.Primary).ToList();
        if (indexes.Count > 0)
        {
            sb.AppendLine("indexes:");
            foreach (var i in indexes)
            {
                sb.Append("  ").Append(i.Name).Append(i.Unique ? " unique (" : " (")
                  .Append(string.Join(", ", i.Columns)).AppendLine(")");
            }
        }

        if (response.ForeignKeys.Count > 0)
        {
            sb.AppendLine("foreign-keys:");
            foreach (var f in response.ForeignKeys)
            {
                sb.Append("  ").Append(string.Join(", ", f.Columns))
                  .Append(" -> ").Append(f.References)
                  .Append('(').Append(string.Join(", ", f.ReferencedColumns)).AppendLine(")");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public static string Rows(QueryResponse response)
    {
        var sb = new StringBuilder();

        if (response.Columns.Count == 0) return "(no columns)";

        sb.AppendLine(string.Join(" | ", response.Columns.Select(c => c.Name)));

        foreach (var row in response.Rows)
        {
            var values = row is JsonArray array
                ? array.Select(cell => Shorten(cell?.ToString() ?? "NULL", 80))
                : [];
            sb.AppendLine(string.Join(" | ", values));
        }

        sb.Append(response.RowCount).Append(" row(s) in ").Append(response.ElapsedMs).Append("ms");
        if (response.Truncated) sb.Append(" TRUNCATED");
        sb.AppendLine();

        if (response.Hint is { } hint) sb.AppendLine(hint);
        return sb.ToString().TrimEnd();
    }

    public static string Plan(ExplainResponse response) => response.Plan;

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// PostgreSQL's <c>format_type</c> returns the SQL-standard spelling, which is
    /// accurate and long. These aliases are the names the same engine accepts and
    /// its own documentation uses, so nothing is lost and roughly half the
    /// characters of a timestamp column are.
    /// </summary>
    static string ShortType(string type) => type switch
    {
        "timestamp with time zone" => "timestamptz",
        "timestamp without time zone" => "timestamp",
        "time with time zone" => "timetz",
        "time without time zone" => "time",
        "character varying" => "varchar",
        "double precision" => "float8",
        _ => type.StartsWith("character varying(", StringComparison.Ordinal)
            ? "varchar" + type["character varying".Length..]
            : type,
    };

    /// <summary>
    /// A serial or identity column's default is a sequence call whose full text —
    /// <c>nextval('orders_id_seq'::regclass)</c> — is a dozen tokens that tell an
    /// agent nothing it can act on. That it is generated is the whole message.
    /// </summary>
    static string ShortDefault(string value) =>
        value.StartsWith("nextval(", StringComparison.Ordinal)
            ? "auto"
            : "default=" + Shorten(value, 40);

    static string Freshness(SchemaFreshness schema)
    {
        var age = Age(schema.AgeSeconds);
        if (schema.Offline) return $"schema {age} old (SOURCE UNREACHABLE - serving cache)";
        return schema.Stale ? $"schema {age} old (stale, refreshing)" : $"schema {age} old";
    }

    static string Age(long seconds) => seconds switch
    {
        < 60 => $"{seconds}s",
        < 3600 => $"{seconds / 60}m",
        < 86400 => $"{seconds / 3600}h",
        _ => $"{seconds / 86400}d",
    };

    /// <summary>Row counts are estimates, so precision past three digits is noise that costs tokens.</summary>
    static string Count(long value) => value switch
    {
        < 1_000 => value.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        < 1_000_000_000 => (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        _ => (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B",
    };

    static string Shorten(string value, int max)
    {
        value = value.ReplaceLineEndings(" ");
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    static string Schema(string qualified)
    {
        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? qualified[..dot] : "";
    }

    static string Bare(string qualified)
    {
        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 ? qualified[(dot + 1)..] : qualified;
    }
}
