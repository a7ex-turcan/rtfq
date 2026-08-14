# ADR 0002 — DDL: additive and corrective only

- **Status:** Accepted
- **Date:** 2026-08-14
- **Decides:** whether RTFQ performs schema changes at all, and if so which ones
- **Amends:** `CLAUDE.md` non-goal #2, which previously read "DML only, never DDL … not behind a flag"

---

## Context

The no-DDL rule was a deliberate ceiling: *schema change is a deploy, not an agent action*. In practice the
common repair — "this table is missing a column", "this query needs an index", "that varchar is too narrow" — is
a schema change, and refusing all of them sends the user back to a migration pipeline for a one-line fix. The
owner asked to be able to say "go fix that table".

The ceiling is lowered, not removed. **Additive and corrective DDL is in scope. Destructive DDL is not, and
neither is anything that changes what the write allow-list refers to.**

## The problem this creates

DDL cannot ride the DML write path, because two of the four structural protections give it **zero** coverage:

- **`max_affected_rows` is meaningless.** `ALTER TABLE orders DROP COLUMN email` affects *zero rows* and destroys
  every value in that column. The cap that bounds blast radius for DML measures nothing here.
- **Before-image journaling has nothing to journal.** The 3am recovery artifact disappears precisely where
  recovery is hardest.

So the fourth gate needs different contents for DDL, and the guard needs a verdict that is neither read nor
mutation. That is what `Schema` is.

**And DDL can rewrite the meaning of the policy file.** `ALTER TABLE orders RENAME TO orders_old` empties the
allow-list entry `public.orders`; a table subsequently created as `orders` inherits that grant. `ALTER TABLE …
SET SCHEMA` and T-SQL's `ALTER SCHEMA … TRANSFER` do the same. A gate that can edit itself is not a gate — which
is why rename and schema-transfer are refused outright rather than merely gated.

## Decision

Two rules, both structural:

1. **Never destroy data.**
2. **Never change what a write allow-list entry resolves to.**

| Allowed (`Schema`) | Refused |
|---|---|
| `ADD COLUMN` | `DROP COLUMN` — destroys data, zero affected rows |
| `ALTER COLUMN … TYPE` (widening) | `ALTER COLUMN … TYPE … USING` — arbitrary transform, silent data loss |
| `ADD CONSTRAINT` / `DROP CONSTRAINT` | `DROP TABLE`, `TRUNCATE` |
| `SET`/`DROP NOT NULL`, column default | `RENAME TABLE`, `RENAME COLUMN`, `SET SCHEMA` |
| `CREATE INDEX`, `DROP INDEX` | `CREATE INDEX CONCURRENTLY` — cannot run in a transaction |
| | `CREATE TABLE` — a deploy, not a repair |

`DROP CONSTRAINT` and `DROP INDEX` are allowed deliberately: they destroy no data and are re-appliable, and an
agent that can add an index must be able to remove one it added. `RENAME COLUMN` is refused as a judgment call
rather than a safety proof — it destroys nothing but silently breaks every caller.

**Access becomes three levels, not two:** `read` < `write` < `schema`, still resolved as the intersection of the
source's declared `access` and the token's grant, exactly as principle 1 already works. No new axis, no
`--allow-ddl` flag. A source at `access: write` cannot perform DDL however the token is granted, and the write
allow-list (`writable_tables`) governs DDL targets too — you may only alter a table you could already write.

MongoDB is excluded: `createIndex` and `dropCollection` are not transactional, so `propose`/`commit` cannot cover
them. A Mongo source declaring `access: schema` is a **config validation error**, the same rule as its
standalone-write case.

## Evidence

The corpus was extended by **17 PostgreSQL and 12 T-SQL DDL cases** and re-run; all pass, under both the JIT and
the **published NativeAOT binary** (per [ADR 0001](0001-sql-parser-selection.md), the AOT run is the one that
counts).

**The load-bearing finding: a statement-type allow-list is not sufficient for DDL.** In PostgreSQL, `ADD COLUMN`
and `DROP COLUMN` are the *same* node type — `AlterTableStmt` — distinguished only by a `subtype` inside the
`cmds` list (`AT_AddColumn` vs `AT_DropColumn`). The allow-list therefore needs a **second level**: statement
type, then subcommand. T-SQL carves it up differently — `AlterTableAddTableElementStatement`,
`AlterTableAlterColumnStatement` and `AlterTableDropTableElementStatement` are three distinct types — but has the
same shape of problem one level down, since `DROP COLUMN` and `DROP CONSTRAINT` share
`AlterTableDropTableElementStatement` and are separated only by `TableElementType`. Both parsers expose what is
needed; verified, not assumed.

Two cases worth keeping in the suite because they look benign and are not:

- `ALTER TABLE orders ALTER COLUMN total TYPE int USING left(total, 3)::int` — a *corrective-looking* statement
  carrying an arbitrary row-by-row transform. Refused via the `USING` clause, not the statement type.
- `CREATE INDEX CONCURRENTLY` — PostgreSQL refuses it inside a transaction block, so it cannot use propose/commit
  at all. Refused rather than special-cased into a non-transactional path.

## Consequences

- **A third verdict.** The guard returns read / mutation / **schema** / reject. `propose_write` handles schema
  changes rather than gaining a sibling tool — a new MCP tool costs the agent context on every call, and the
  lifecycle (parse, gate, open transaction, return handle, commit) is identical. The response carries
  `affected_rows: null` and a schema diff instead of a row sample, so the kind is explicit and machine-readable.
- **DDL is transactional on both SQL engines**, so propose/commit works unchanged. PostgreSQL and SQL Server both
  roll back DDL; this is why MongoDB is excluded rather than degraded.
- **`lock_timeout` becomes a required setting, separate from `statement_timeout`.** An `ALTER TABLE` waiting on
  `ACCESS EXCLUSIVE` queues **every subsequent reader behind it** in PostgreSQL's FIFO lock queue — so a blocked
  DDL statement takes the table down for readers even though it has changed nothing yet. This is the single most
  likely way this feature ruins someone's afternoon, and it is an operational control, not a nicety.
- **Schema before-images.** Journal the object's prior definition (column type, constraint text, index
  definition) before altering. It is not a row-level undo, but it makes `DROP CONSTRAINT` and a botched type
  change reversible by hand.
- **The security posture document must state the new ceiling plainly**: RTFQ can add and widen, and cannot drop,
  rename, or truncate.

## Open questions

- **Approval default for schema changes.** Under this policy the self-editing-gate attack is refused outright and
  data destruction is blocked, so `require_approval` can stay per-source rather than being forced on. If the
  policy is ever widened to destructive DDL, that reasoning collapses and approval must become mandatory.
- **Table rewrites.** `ADD COLUMN … NOT NULL DEFAULT` is cheap on PostgreSQL 11+ but rewrites the whole table on
  older versions and on some SQL Server type changes. Whether the guard should estimate rewrite cost, or simply
  rely on `lock_timeout` and `statement_timeout`, is undecided.
