# ADR 0001 — SQL parser selection

- **Status:** Accepted
- **Date:** 2026-08-14
- **Decides:** the open question "Parser per dialect" from [`docs/PHASES.md`](../PHASES.md), which gates M3
- **Note:** an earlier revision of this ADR decided the same question for Go, before the language was settled as
  .NET. It is in git history. Everything below was re-run on .NET; nothing is carried over on trust.

---

## Context

`CLAUDE.md` requires per-dialect parsing with a real parser, never regex, and a single self-hosted binary with no
runtime to install. The parser question was the largest technical risk in the project, because a wrong answer is
not slow or ugly — it is a **statement guard that can be talked past**, which is the one failure this product
cannot absorb.

This spike ran an adversarial corpus of **35 PostgreSQL** and **20 T-SQL** statements through candidate parsers,
with the classifier written the way the real guard will be written, plus fail-closed probes and a publish matrix.

## Decision

| Dialect | Library | Kind |
|---|---|---|
| SQL Server | [`Microsoft.SqlServer.TransactSql.ScriptDom`](https://www.nuget.org/packages/Microsoft.SqlServer.TransactSql.ScriptDom) 180.78.1 (`TSql180Parser`) | First-party Microsoft, pure managed |
| PostgreSQL | [`Npgquery`](https://www.nuget.org/packages/Npgquery) 1.1.0 — libpg_query via P/Invoke | PostgreSQL's own parser, native |

Both are the *real* grammar for their dialect: ScriptDom is the parser behind SSMS and SqlPackage, and libpg_query
is PostgreSQL's own parser extracted as a library. Neither is a reimplementation we would have to trust.

## Evidence

Versions: .NET 10.0.303 · ScriptDom 180.78.1 · Npgquery 1.1.0 (libpg_query, PG 18 parse trees).

| | PostgreSQL | SQL Server |
|---|---|---|
| Adversarial corpus (JIT) | **35 / 35** | **20 / 20** |
| Adversarial corpus (NativeAOT binary) | **35 / 35** | **20 / 20** |
| Fails closed on malformed input | Yes — parse error returned | Yes — `IList<ParseError>` returned |
| Parse + classify latency | 56 µs simple, 128 µs complex | 27 µs simple, 24 µs complex |
| Cold start | 10.8 ms JIT / **0.6 ms** AOT (native library load) | negligible |

Cases a naive guard fails and these did not: `WHERE 1=1` and `WHERE id = 1 OR true`; `;`-stacked statements;
`DROP` hidden in a line or block comment; a semicolon inside a string literal; `DELETE` buried in a CTE beneath a
top-level `SELECT`; `SELECT INTO`; `COPY ... FROM PROGRAM`; `DO $$ ... $$`; `EXPLAIN ANALYZE`; `GRANT`; `SET ROLE`;
bracket-quoted and three-part T-SQL names; a T-SQL batch with **no separator at all** (`SELECT 1 DROP TABLE orders`);
`EXEC`, `sp_executesql`, `xp_cmdshell`; `INSERT ... EXEC`; and a Cyrillic homoglyph table name that must *not*
match an `orders` allow-list entry.

**Deployment shapes**, both verified to pass the full corpus on the published artifact:

| Shape | Files | Size | Cold start |
|---|---|---|---|
| NativeAOT | **2** — executable + `libpg_query.so` | ~18 MB | 0.6 ms |
| Self-contained single-file | **1** | 44 MB | 15.5 ms |

`Npgquery` ships native binaries for `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`, `win-arm64`.
**`osx-x64` (Intel Mac) is missing** — a gap to note in M5, not a blocker in 2026.

## The finding that matters most

> **A reflection-based AST walk is a fail-open security hole under NativeAOT, and every JIT test still passes.**

The first version of the T-SQL classifier walked the tree with `node.GetType().GetProperties(...)`. Under the JIT
it scored 20/20. Published with NativeAOT, the *same code on the same corpus* scored **3/20** — and every failure
was in the unsafe direction:

```
  drop-table    want=Reject  got=Read      truncate      want=Reject  got=Read
  xp-cmdshell   want=Reject  got=Read      insert-exec   want=Reject  got=Read
  unqualified-delete  want=Reject  got=Read
```

The trimmer had removed property metadata for types nothing statically referenced, so the walk returned zero
nodes, so the guard found no statements, so everything looked like a harmless read. **A build-configuration flag
silently converted the statement guard into an open door.** A second instance of the same class of bug: selecting
the parser via `Assembly.GetExportedTypes()` worked under the JIT and threw
`InvalidOperationException: Sequence contains no elements` under AOT.

Both are fixed by not using reflection — ScriptDom's own `TSqlFragmentVisitor`, and naming `TSql180Parser`
directly — after which the AOT binary scores 20/20 and 35/35. Three rules follow, and they are not style
preferences:

1. **Walk parse trees with the parser's visitor. Never with `GetProperties`.**
2. **The adversarial suite must run against the published AOT binary**, in CI. A JIT-only run does not test what
   ships. This is an M0 requirement, not an M5 one, because it constrains how everything above is written.
3. **Treat trim/AOT warnings as build errors** (`TreatWarningsAsErrors`, `IsAotCompatible`). Every warning in this
   spike pointed at real breakage.

## What else this changes in the design

### 1. The guard must ALLOW-LIST statement types, not deny-list DDL

`CLAUDE.md` principle 2 says *"classify as read / DML / DDL. DDL is rejected always."* That framing is a deny-list,
and a deny-list **misses**:

| Statement | Is it DDL? | What it does |
|---|---|---|
| `COPY orders FROM PROGRAM 'curl ...'` | No | Shell execution as the database OS user |
| `COPY (SELECT ...) TO PROGRAM 'curl -d @-'` | No | Exfiltrates the table |
| `DO $$ BEGIN DELETE FROM orders; END $$` | No | Arbitrary PL/pgSQL; the `DELETE` is invisible to classification |
| `EXPLAIN ANALYZE DELETE FROM orders` | No | **Actually executes** the statement |
| `GRANT ALL ON orders TO PUBLIC` | No (DCL) | Privilege escalation |
| `SET ROLE postgres` | No | Re-points every later gate at another identity |
| `EXEC xp_cmdshell 'dir'` | No | Command execution, T-SQL flavour |

The guard therefore executes exactly `SelectStmt`, `InsertStmt`, `UpdateStmt`, `DeleteStmt`, `MergeStmt` and
`ExplainStmt` **without ANALYZE** (`SelectStatement`, `InsertStatement`, `UpdateStatement`, `DeleteStatement`,
`MergeStatement` in T-SQL), and refuses every other node type. New engine releases add statement types; an
allow-list fails closed against them, a deny-list fails open.

### 2. Statement-type checking cannot see inside function calls

These classify as ordinary reads and pass every gate:

```sql
SELECT dblink_exec('host=evil', 'DELETE FROM orders');  -- writes to another host
SELECT lo_export(1, '/tmp/x');                          -- writes a file
SELECT pg_read_file('/etc/passwd');                     -- reads a file
```

Function names **are** extractable from the tree (`FuncCall` → `funcname`; verified), so a deny-list is
implementable — but it is a deny-list against an open set, the shape rejected in finding 1.

**Therefore the scoped database `GRANT` is the real boundary, not the guard.** `CLAUDE.md` principle 5 already
says our guards are defence in depth and not a substitute for a correct `GRANT`; this spike upgrades that from
good advice to a **load-bearing requirement**, and the M5 security posture document must say so plainly. A
function deny-list covering the known-dangerous set is still worth having as a second layer.

### 3. Two smaller mechanics, both confirmed implementable

- **`INSERT ... EXEC` (T-SQL)** runs a stored procedure but surfaces only as `InsertStatement`. The tree exposes
  `InsertSpecification.InsertSource`, so permit `ValuesInsertSource` and `SelectInsertSource`, refuse
  `ExecuteInsertSource`. Verified.
- **Unqualified names resolve at execution time** — via `search_path` in PostgreSQL, the default schema in SQL
  Server. An allow-list entry checked against a name the *server* resolves differently is a bypass, so pin
  `search_path` (and the default schema) on every connection, or require schema-qualified names. Note `"Orders"`
  and `orders` are genuinely different tables: the comparison must never case-fold.

## Residual risks

- ⚠ **`Npgquery` is young** — version 1.1.0, two releases. It is a thin P/Invoke shim over versioned libpg_query,
  so the exposure is packaging rather than grammar, and the wrapper is small enough to replace or vendor if it is
  abandoned. Alternatives exist (`PgsqlParser`, or our own P/Invoke), and libpg_query itself is the stable part.
  **Mitigation:** pin the version, keep the corpus as a regression suite on every bump.
- ⚠ **A native dependency contradicts "single binary" if read literally.** AOT ships two files. Decide the
  release shape in M5 with the numbers above; do not discover it late.
- ⚠ **`osx-x64` is unsupported** by Npgquery's native payload.
- ⚠ **NativeAOT does not cross-compile across operating systems.** Mitigated by building each target in its own
  container — verified here by producing and running the linux-x64 AOT binary from a Windows host.
- ⚠ **A `-- just a comment` or empty input parses without error** in both engines, yielding zero statements. The
  guard rejects on "no statement" rather than trusting the parser to error.

## Consequences

- M2's parser spike is **closed**; M3 is unblocked.
- M0 gains a requirement: **CI publishes the AOT binary and runs the guard suite against it.**
- The corpus in `spike/parser/Corpus.cs` is the seed of M3's adversarial suite.

## Reproduction

The spike lives in [`spike/parser/`](../../spike/parser) — throwaway code, not part of the build, kept as evidence.

```bash
cd spike/parser
dotnet run -- pg        # PostgreSQL corpus, 35 cases
dotnet run -- tsql      # SQL Server corpus, 20 cases
dotnet run -- anomaly   # why libpg_query accepts "SELECT 1 @@@@ DELETE FROM orders"
dotnet run -- probe     # Npgquery's real API surface

# The one that matters: the AOT binary, not the JIT build.
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  apt-get update -qq && apt-get install -y -qq clang zlib1g-dev &&
  dotnet publish -c Release -r linux-x64 /p:PublishAot=true -o /out &&
  /out/spike pg && /out/spike tsql'
```
