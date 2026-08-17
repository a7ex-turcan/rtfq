# RTFQ — Implementation Phases

> Companion to [`CLAUDE.md`](../CLAUDE.md). That document says *what* RTFQ is and what it refuses to become.
> This one says *in what order we build it and how we know a phase is done.*
>
> Phase names are the milestones M0–M5 already settled in `CLAUDE.md` — deliberately not a second numbering
> scheme. If a phase here contradicts `CLAUDE.md`, `CLAUDE.md` wins and this file is stale; say so.

---

## How to read this

Each phase has a **goal**, an ordered **scope**, an explicit **not in this phase**, **exit criteria** that are
verifiable rather than vibes, and the **risks/decisions** that could sink it.

Three rules govern the sequence:

1. **A phase ships when its exit criteria pass, not when its features exist.** M3 in particular ships on the
   strength of its adversarial test suite, per `CLAUDE.md`.
2. **Contracts land early, implementations land late.** The error taxonomy, response envelope, and adapter
   interface are M0 work even though most of their surface is unused until M2–M3. Retrofitting a contract across
   four adapters is the expensive way to learn this.
3. **M1 is the go/no-go.** If the read and discovery surface is unpleasant for an agent, stop and redesign it.
   Nothing in M2–M5 rescues a bad tool surface.

**Legend:** ☐ deliverable · ✅ exit criterion · ⚠ risk · ❓ decision needed

---

## Phase map

| Phase | Theme | Ships when | Depends on |
|---|---|---|---|
| **M0** | Walking skeleton | One real query crosses the wire, capped and audited | — |
| **M1** | MCP read surface + schema cache | An agent answers a real question inside a sane token budget | M0 |
| **M2** | Adapters: SQL Server, Mongo, HTTP | The interface held without leaking dialect concerns upward | M0 |
| **M3** | The write path | The adversarial suite is convincing per dialect | M1, M2, parser spike |
| **M4** | Approval and unlock | A human gates a commit and the seam is provider-agnostic | M3 |
| **M5** | Shipping | A stranger gets to first query in five minutes | all |

M2 depends only on M0, so it can run alongside M1 — but M1 is the go/no-go, so **do not start M2 before M1's
exit review**. Building three more adapters onto a tool surface you are about to redesign is wasted work.

---

## Phase M0 — Walking skeleton

**Goal:** one query, from CLI to Postgres and back, over the real wire protocol, with the real contracts in place.
Thin but not fake — nothing here gets thrown away.

### Scope

1. **Toolchain and repo skeleton.** Pin the SDK in `global.json`, add `Directory.Build.props` with
   `TreatWarningsAsErrors`, `Nullable`, `IsAotCompatible` and analyzers enabled, plus `.editorconfig` and CI
   (build + analyzers + test on push). **CI must also publish the NativeAOT binary and run the guard suite
   against it** — [ADR 0001](decisions/0001-sql-parser-selection.md) found a guard that passes 20/20 under the
   JIT and fails open as a published binary, so a JIT-only test run does not tell us what shipped.
2. **Package layout** (see [below](#package-layout)). Create the directories with their interfaces, even where
   the only implementation is Postgres.
3. **Config loader** — YAML → typed struct. Secret resolution for `${env:...}` and `${file:...}`
   (`${vault:...}` is a stub returning "unsupported" until there is demand). Plaintext secret ⇒ warning in dev,
   hard fail in production mode.
4. **Config validation as a first-class pass**, separate from loading, with `rtfq validate --config rtfq.yaml`.
   Every "this is a config validation error, not a warning" rule in `CLAUDE.md` gets registered here as a named
   check, even the ones whose subject doesn't exist yet (Mongo-standalone, HTTP wildcard + write). An empty
   check that is wired up is cheap; a validation framework retrofitted in M2 is not.
5. **Wire contract** — `Rtfq.Contracts`: request/response DTOs, the response envelope
   (`row_count`, `truncated`, `elapsed_ms`, `next_cursor`), and the **error taxonomy**.
6. **Transport** — HTTP+JSON over TLS, token auth middleware (constant-time compare), request IDs, structured
   logging. **TLS is mandatory unless the listener is loopback** — a rule, not a config knob.
7. **Policy engine, thin but real** — token grant × source access → effective permission, default-deny.
   Target allow-lists and the mutation gates arrive in M3; the *shape* (an intersection, evaluated in one place)
   is fixed now.
8. **Adapter interface + Postgres adapter** — `connect`, `introspect`, `sample`, `execute-read`, plus a
   `Capabilities()` declaration. Write-side methods exist on the interface and return "unsupported" until M3.
   Npgsql for the driver.
9. **Row cap and statement timeout.** M0 enforces the cap by **stopping the row scan** at `max_rows + 1` and
   setting `truncated: true` — real LIMIT injection needs the parser and lands in M1. `statement_timeout` is set
   on the connection from day one; per `CLAUDE.md` it is a blast-radius guard, not a nicety.
10. **Audit log, minimal.** Append-only JSONL: caller, source, statement, classification, outcome, duration.
    Before-images land in M3. *This is an addition to M0 as written in `CLAUDE.md`* — deliberate, because an
    audit sink threaded through the request path afterwards is a rewrite, and "every request is audited" is only
    true if it was never optional.
11. **CLI** — `rtfq serve`, `rtfq query`, `rtfq sources`, `rtfq validate`. Thin client over `Rtfq.Client`.
12. **Test harness** — Testcontainers for .NET against real Postgres. No mocks for adapters, per the working agreements.

### Not in this phase

MCP (M1) · schema cache (M1) · any non-Postgres adapter (M2) · any write path (M3) · pagination beyond
"truncated: true" (M1) · approval (M4).

### Exit criteria

- ✅ `rtfq serve --config rtfq.yaml` + `rtfq query --source orders-db "SELECT ..."` returns rows over TLS with
  token auth, against a containerized Postgres.
- ✅ A query returning more than `max_rows` comes back truncated, flagged, and never silently.
- ✅ A token without a grant on the source is refused with a typed error code — and the refusal is in the audit log.
- ✅ `rtfq validate` rejects a config with a plaintext password in production mode and accepts the same config in dev
  with a warning.
- ✅ CI is green on a clean clone: build, lint, unit tests, and the Postgres integration suite.

### Risks and decisions

- ❓ **State directory.** The schema cache (M1), write handles (M3), and unlock state (M4) all need somewhere to
  live. Decide the `--state-dir` convention now (OS-appropriate default, overridable) even though M0 writes only
  the audit log there. Constraint from `CLAUDE.md`: no database of our own — files, atomically written.
- ❓ **Error taxonomy shape.** Stable machine-readable codes (`policy.source_not_writable`,
  `guard.unqualified_mutation`, `cap.affected_rows_exceeded`) plus a human message. Agents branch on these, so
  they are API surface from the first release — worth 30 minutes of naming discipline now.

---

## Phase M1 — MCP read surface and schema cache

**Goal:** an agent in Claude Code can discover an unfamiliar database and answer a real question without burning
its context. **This is the go/no-go milestone.**

### Scope

1. **Schema introspection** — Postgres implementation of `introspect`, producing a normalized, source-agnostic
   model (tables, columns, types, keys, indexes, row-count estimates).
2. **Schema cache** — on-disk, TTL'd, atomically written, **served with an age flag when the source is
   unreachable**. `rtfq refresh <source>` forces re-introspection. Staleness is always visible in the response.
3. **`describe_*` compactness — the real design work of this phase.** Budget it explicitly: a 200-table database
   must produce a `describe_source` that costs an agent on the order of a thousand tokens, not tens of thousands.
   That forces summarization, grouping, and paging decisions. Ship a **token-budget test** in CI that asserts the
   rendered output for the fixture database stays under a stated ceiling — otherwise this regresses silently.
4. **Read tools over the HTTP API** — `list_sources`, `describe_source`, `describe_table`, `sample`, `query`,
   `explain` (Postgres `EXPLAIN` without `ANALYZE`).
5. **LIMIT injection via the parse tree**, replacing M0's scan-stop. Requires the parser decision (see M2/M3 spike)
   to be at least provisionally made for Postgres.
6. **Cursor pagination** — see the open decision below.
7. **MCP server** — `rtfq mcp` over stdio, a thin adapter mapping MCP tools onto the HTTP client. No policy, no
   caching, no cleverness in this layer; if it grows logic, that logic belongs in the server.
8. **Dogfood pass** — point Claude Code at a non-trivial Postgres and work a real question end to end. Record
   token spend per tool call.

### Not in this phase

Non-Postgres adapters (M2) · anything that mutates (M3).

### Exit criteria

- ✅ Claude Code, given only the MCP tools, answers a multi-table question against an unfamiliar database without
  a human explaining the schema.
- ✅ Recorded token cost per discovery call is under the stated budget, asserted in CI.
- ✅ **Container stopped:** `describe_source` and `describe_table` still answer, flagged stale with an age; `query`
  fails with a typed unreachable error. This is the offline-discovery property and it is worth a dedicated test.
- ✅ Every truncated response is machine-readably truncated **and tells the caller what to do about it**.
  Per [ADR 0003](decisions/0003-no-cursor-pagination.md) there is no cursor: truncation is terminal, and
  `next_cursor` stays in the envelope as a permanent null.
- ✅ Go/no-go review held, written down. If the surface is unpleasant, the fix is a redesign here — not a workaround in M2.

### Risks and decisions

- ✔ **Cursor pagination — settled by [ADR 0003](decisions/0003-no-cursor-pagination.md), reversing this doc's
  earlier lean.** There is no cursor. Offset re-execution costs a full scan per page against production and is
  not even snapshot-consistent; a held cursor pins a connection idle-in-transaction, which blocks `VACUUM`. And
  an agent paging thousands of rows into context has already lost. Truncation is terminal and actionable.
- ⚠ **Compactness is a design problem, not a formatting problem.** If `describe_source` is just a table dump with
  fewer columns, this phase has failed and M2–M5 inherit the failure.

---

## Phase M2 — Adapters: SQL Server, MongoDB, HTTP

**Goal:** prove the adapter interface holds. The features are a side effect; the interface validation is the point.

### Scope

1. ~~**Parser spike (do this first — it gates M3).**~~ **Done — [ADR 0001](decisions/0001-sql-parser-selection.md).**
   PostgreSQL uses `Npgquery` (libpg_query via P/Invoke — PostgreSQL's own parser), SQL Server uses
   `Microsoft.SqlServer.TransactSql.ScriptDom` (first-party Microsoft). Both clear the adversarial corpus as
   published NativeAOT binaries. The sting is in the tail: the ADR found that reflection-based AST walking is a
   **fail-open guard** under AOT, so the guard uses the parser's own visitor and CI tests the published artifact.
2. **SQL Server adapter** — `Microsoft.Data.SqlClient`; introspection, read, `SET SHOWPLAN_XML` for `explain`.
3. **MongoDB adapter** — official driver; introspection is schema *inference* over sampled documents, and it must
   be honest about being inferred. **Standalone (no replica set, no transactions) fails config validation when
   marked `access: write`** — loudly, at load, with no degraded write mode. The check registered in M0 gets its
   implementation here.
4. **HTTP adapter** — path allow-lists, method restriction, header injection from secret refs. **Wildcard path +
   non-`GET` method is a validation error.** "Introspection" is the declared endpoint list; `describe_source`
   renders that.
5. **Interface audit.** For each adapter, record what (if anything) had to change above the adapter layer. Each
   such change is a defect in the interface, not a feature.

### Not in this phase

Writes for any adapter (M3) — including Mongo, whose transactional story is the reason M3 needs it landed first.

### Exit criteria — **met; see [ADR 0005](decisions/0005-m2-interface-audit.md)**

- ✅ All four adapters pass a shared conformance suite against real containerized instances. The suite contains
  no per-adapter branching, which is the actual evidence.
- ✅ The interface audit found **one** leak — `ConfigValidator` had hardcoded which kinds support transactional
  writes and DDL — and it was fixed by moving that knowledge into `AdapterCatalog` in the adapter layer.
- ✅ Mongo-standalone marked writable is refused; because topology needs a live connection, this moved from
  config validation to a **startup** capability check. An unreachable source stays a warning.
- ✅ HTTP wildcard + a write method is refused, as is a wildcard anywhere but the final character.
- ✅ Mongo schema is flagged `inferred`, and a field with more than one observed type reports all of them
  (`total double|int32`) rather than picking one.
- ✔ The glibc floor M2 introduced is fixed: Linux artifacts build inside Ubuntu 22.04 and target glibc 2.34,
  which CI asserts.
- ⚠ **Open:** Linux hosts now need `libicu` installed, because SQL Server's driver refuses invariant
  globalization. Documented in the README; `StaticICULinking` would remove it and does not currently build.

### Risks and decisions

- ✔ **Parser choice per dialect — resolved by [ADR 0001](decisions/0001-sql-parser-selection.md)**, and both
  dialects get the genuine article: Microsoft's own ScriptDom for T-SQL, PostgreSQL's own parser via libpg_query.
  Adversarial corpus: 35/35 Postgres, 20/20 T-SQL, verified on the published AOT binary as well as under the JIT.
  Residual risk sits on the Postgres wrapper — `Npgquery` is at 1.1.0 with two releases — so pin it and keep the
  corpus as a regression suite on every bump. libpg_query itself is the stable part.
- ⚠ **A native dependency means "single binary" needs a decision, not an assumption.** AOT publishes the
  executable *plus* `libpg_query.so`; a true single file costs 44 MB and a slower start. Numbers are in the ADR;
  choose the release shape in M5 rather than discovering it there.
- ⚠ **The guard is an allow-list, not a DDL deny-list** — an ADR 0001 finding that changes this phase's shape.
  `COPY ... FROM PROGRAM`, `DO`, `EXPLAIN ANALYZE`, `GRANT`, `SET ROLE` and `EXEC xp_cmdshell` are none of them
  DDL, and all of them are catastrophic. Execute only the enumerated node types; refuse everything else by default.
- ✔ **Mongo's "statement" is not a string** — and the interface held anyway, because a Mongo command document
  *is* its native dialect. `{"find": "orders", "filter": {...}}` is a statement in the same sense a `SELECT` is,
  and the adapter parses its own. Nothing above the adapter learned that documents exist
  ([ADR 0005](decisions/0005-m2-interface-audit.md)).

---

## Phase M3 — The write path

**Goal:** mutations that are structurally incapable of exceeding their blast radius. Ships on the test suite.

### Scope

1. **Four structural gates**, evaluated in order, each independently tested and each producing a distinct error code:
   source writable → token granted → target allow-listed → statement passes the guard.
2. **Statement guard** — per-dialect real parse, then an **allow-list of statement node types** (not a DDL
   deny-list — see [ADR 0001](decisions/0001-sql-parser-selection.md)). Unqualified `UPDATE`/`DELETE` (and
   empty-filter Mongo updates) rejected outright, no override; trivially-true predicates (`WHERE 1=1`,
   `WHERE ... OR true`) count as unqualified. Plus the three mechanics ADR 0001 identified: a function-name
   deny-list, `InsertSource` restriction for T-SQL, and a pinned `search_path`/default schema so allow-list
   entries resolve to the same relation the server does.
3. **Schema-change path** — [ADR 0002](decisions/0002-ddl-additive-and-corrective.md). A third verdict
   (`Schema`), a **second-level allow-list over DDL subcommands** (statement type alone cannot separate
   `ADD COLUMN` from `DROP COLUMN`), `access: schema` as a third permission level, schema before-images, and
   `lock_timeout` as a required setting. Reuses `propose_write`/`commit_write` rather than adding tools.
   Mongo sources declaring `access: schema` fail config validation.
3. **Mutation broker** — open transaction, execute uncommitted, read the driver's **real affected-row count**,
   compare to `max_affected_rows`, roll back and refuse with the count if exceeded. Never an estimate.
4. **Handles** — bound to the exact statement that produced them (hash-bound, non-repointable), TTL'd, rolled back
   on expiry, single-use. Held-transaction accounting: a cap on concurrent open handles per source, because each
   one is a held connection and an open lock set.
5. **Diff sampling and before-image journaling** — affected rows captured *inside the same transaction*, before
   mutation, to the audit log. Truncate-with-marker for oversized values (`jsonb` blobs, large Mongo documents).
6. **Optional unbounded pre-check** — parse-tree "no `WHERE` / trivially-true predicate" early-out before the
   transaction opens. Explicitly an optimization: **the transaction count remains the source of truth.**
7. **`propose_write` / `commit_write` / `abort_write`** on the API and MCP surfaces.
8. **Adversarial test suite, per dialect** — the actual deliverable. It must include, at minimum: DDL smuggled
   through comments, `;`-stacked statements, CTE-wrapped writes, `WHERE 1=1`, quoted identifiers matching an
   allow-list entry by string but not by resolution, cross-schema aliasing, unicode/homoglyph table names,
   `search_path` games, and a mutation that exceeds the cap by exactly one row. For DDL: `DROP COLUMN` presented
   as an `ALTER TABLE`, `ALTER COLUMN ... USING`, `RENAME`, `SET SCHEMA`, and `CREATE INDEX CONCURRENTLY`.
   The 84-case corpus in `spike/parser/Corpus.cs` is the starting point, not the finished suite.

### Not in this phase

Human approval (M4) — M3 ships with `require_approval` parsed and enforced as *"reject the commit"*, not as
"prompt someone". Auto-commit sources work; approval-required sources cannot yet be approved. That keeps M3's
test surface structural.

### Exit criteria — **met for PostgreSQL and SQL Server; see [ADR 0006](decisions/0006-m3-write-path.md)**

- ✅ The adversarial suite passes on both write-capable adapters — 50 end-to-end write tests against real
  containerised databases. Every refusal test also re-reads the data from a separate connection, because a gate
  that reports "no" while letting the write through is the failure that matters. *A reviewer other than the
  author has not yet looked at it; that half of the gate is outstanding.*
- ✅ A mutation one row over the cap is refused with the real count, and the rollback is verified by re-reading.
- ✅ An expired handle rolls back automatically; handles are single-use and owned by the caller that made them,
  so re-pointing is impossible by construction — commit takes only a handle.
- ✅ Every mutation is journalled with its before-images at propose time, before anything is committed.
- ✅ No schema statement can destroy data or repoint an allow-list entry — verified at the **subcommand** level
  in both dialects, including T-SQL's `DROP COLUMN`/`DROP CONSTRAINT` sharing one statement type.
- ✅ A server that goes away mid-transaction commits nothing, tested both as an orderly shutdown and as the
  connection vanishing (`pg_terminate_backend`), because those are rolled back by different things.
- ⚠ **Not included:** MongoDB writes (need a replica set and their own suite) and HTTP writes (no transactions
  to leave open). Both refuse with a typed code.

### Risks and decisions

- ⚠ **The uncommitted runaway.** A statement that would touch ten million rows still *does the work* before we roll
  it back — locks, WAL, memory. `statement_timeout` and the pre-check are the mitigations, and neither is complete.
  This must be documented as a known property, not quietly hoped about.
- ❓ **Before-image ceiling.** Is there a hard journal-size cap per mutation independent of `max_affected_rows`?
  (`CLAUDE.md` open question. Truncate-with-marker is the leaning answer.)

---

## Phase M4 — Approval and unlock

**Goal:** close the intent gate. Per `CLAUDE.md`'s central tension, this is *the* control against a well-formed
malicious write — not a nicety.

### Scope

1. **`ApprovalProvider` interface** — request approval, poll status, record decision and approver.
2. **CLI-blocking reference implementation** — `rtfq approvals` lists pending, shows **the statement and the diff**,
   never an agent-supplied summary. Long-poll endpoint on the server side.
3. **Commit blocking** — a `require_approval` source's handle cannot commit until approved; the handle TTL and the
   approval window interact (a decision: does pending approval extend TTL, or does approval race expiry?).
4. **Time-boxed unlock** — `rtfq unlock orders-db --write --ttl 15m`, in-memory, automatic expiry.
   **Restart re-locks** — the safe default, and not configurable.
5. **Audit of decisions** — approver identity, timestamp, and the exact statement approved.

### Not in this phase

Slack. It stays behind the same interface and out of core, per `CLAUDE.md`.

### Exit criteria

- ✅ An approval-required source blocks commit, surfaces statement + diff to a human, and commits only after
  explicit approval — with the decision and approver in the audit log.
- ✅ Denial rolls back. Expiry rolls back. Neither leaves a handle alive.
- ✅ A second provider implementation (even a trivial one) can be swapped in without touching the broker — the
  proof that the seam is real.
- ✅ `unlock` expires on schedule and on restart.

### Risks and decisions

- ❓ **What "plugin" means under NativeAOT.** AOT rules out `AssemblyLoadContext` and runtime plugin loading, so
  the .NET answer is the same as the Go one for different reasons: a generic **webhook provider in core** that an
  external Slack bot implements. It keeps the `CLAUDE.md` promise that Slack is never in core, and any provider
  can be built against it without linking into our binary.
- ⚠ **Approval fatigue is a real failure mode.** If approving is tedious, people will set `require_approval: false`
  and the intent gate is gone. The reference implementation's ergonomics are a security property.

---

## Phase M5 — Shipping

**Goal:** a stranger with a Postgres and five minutes gets to a first query — and a security reviewer can approve
pointing this at production.

### Scope

1. ~~**Release pipeline**~~ — **landed early, at M0.** Tag-driven, one NativeAOT build per platform, each bundle
   carrying the binary plus README, licence, changelog and a sample config; release notes come from the changelog
   section for that version. What remains for M5 is the Docker image and reproducibility, not the mechanics.
   Original notes, still accurate: NativeAOT builds per RID, with two constraints from
   [ADR 0001](decisions/0001-sql-parser-selection.md): AOT does **not** cross-compile across operating systems, so
   each target OS needs its own build container or agent; and `Npgquery` ships no `osx-x64` native payload, so
   Intel Macs are unsupported unless we build libpg_query ourselves.
   **Decide the artifact shape here**: AOT (executable + `libpg_query.so`, ~18 MB, 0.6 ms start) or self-contained
   single-file (one file, 44 MB, 15.5 ms start).
2. **Docker image** — a convenience, not the deployment model.
3. **Quickstart** — install → minimal `rtfq.yaml` → `rtfq serve` → first query, under five minutes, tested on a
   clean machine by someone who did not write it.
4. **MCP setup docs** for Claude Code, Cursor, and a generic client.
5. **Security posture document** — written for the reviewer who has to sign off on production. Must state plainly:
   the four gates stop blast radius, approval stops intent, and an auto-committing writable source is safe against
   catastrophe but **not against malice**.
6. **Sample configs** — read-only starter, and a writable-with-approval production example.

### Exit criteria

- ✅ Clean-machine quickstart timed under five minutes by someone other than the author.
- ✅ Release artifacts verified on each target platform — by **running the guard suite against the published
  binary**, not by checking that it starts.
- ✅ Security posture doc reviewed by someone who did not write the code.
- ✅ README carries the honest one-paragraph version of the threat model, not a marketing paragraph.

---

## Project layout

Provisional, established in M0 so later phases have somewhere to land. One solution, one shipped executable:

```
src/
  Rtfq.Cli/              # the executable: serve, query, sources, describe, validate, refresh, unlock, mcp
  Rtfq.Contracts/        # wire DTOs, response envelope, error taxonomy   (the stable contract)
  Rtfq.Server/           # HTTP+JSON, TLS, auth, endpoints
    Config/              #   load, secret resolution, validation checks
    Policy/              #   grant x access x allow-list -> effective permission
    Guard/               #   statement classification; per-dialect parsing lives in adapters
    Broker/              #   transactions, handles, TTL, caps, approval hookup
    Audit/               #   append-only JSONL, before-images
    Schema/              #   introspection model, cache, staleness
  Rtfq.Adapters/         # ISourceAdapter + capabilities
    Postgres/ SqlServer/ Mongo/ Http/
  Rtfq.Client/           # HTTP client shared by CLI and MCP
  Rtfq.Mcp/              # MCP tool definitions -> client calls (thin, no logic)
tests/
  Rtfq.Guard.Tests/      # the adversarial suite; also run against the published AOT binary
  Rtfq.Adapters.Tests/   # Testcontainers, one real instance per adapter
docs/
```

Everything above `Rtfq.Adapters` is source-agnostic. If that stops being true, the interface is wrong.

Two AOT constraints shape this from M0, both from [ADR 0001](decisions/0001-sql-parser-selection.md): no
reflection over parse trees, and no reflection-based serialization — use `System.Text.Json` source generators for
the wire DTOs and the audit log.

---

## Cross-cutting, owned from M0

These are not phases; they are properties every phase maintains.

| Concern | Rule |
|---|---|
| **Error taxonomy** | Stable machine-readable codes. Agents branch on them, so they are API surface. |
| **Response envelope** | `row_count`, `truncated`, `elapsed_ms`, `next_cursor`, staleness. Silent truncation is a bug. |
| **Audit** | Every request, including refusals. Never optional, never shipped off the box. |
| **Tests** | Real containerized instances per adapter. Read-only-enforcement and mutation-guard suites are adversarial, and run against the **published AOT binary**, not only the JIT build. |
| **AOT discipline** | No reflection over parse trees, no reflection-based serialization. Trim/AOT warnings are build errors. A trimmed reflection walk fails **open** — see [ADR 0001](decisions/0001-sql-parser-selection.md). |
| **Defaults** | Caps, timeouts, and TTLs are contract. Changing one is an API change. |
| **Config knobs** | Prefer deleting a feature. A new knob needs an argument, not a use case. |
| **Tool surface** | Every new MCP tool costs the consuming agent context on every call. Adding one needs an argument. |

---

## Open decisions, by the phase that needs them

| Decision | Needed by | Leaning |
|---|---|---|
| State-directory convention | M0 | OS-appropriate default, `--state-dir` override, atomic file writes |
| Error-code naming scheme | M0 | `domain.reason`, stable across releases |
| Cursor pagination semantics | M1 | Re-execute with offset, `snapshot: false` in the envelope |
| ~~Parser per dialect~~ | ~~M2, gates M3~~ | **Settled — [ADR 0001](decisions/0001-sql-parser-selection.md).** ScriptDom for T-SQL, libpg_query for Postgres |
| Release artifact shape (AOT two-file vs single-file) | M5 | Numbers in [ADR 0001](decisions/0001-sql-parser-selection.md); lean AOT + tarball |
| Function-call deny-list contents | M3 | Cover the known-dangerous set; the scoped `GRANT` is the real boundary ([ADR 0001](decisions/0001-sql-parser-selection.md)) |
| ~~Proactive vs. serve-stale-then-refresh~~ | ~~M1~~ | **Settled — [ADR 0003](decisions/0003-no-cursor-pagination.md).** Serve stale immediately and refresh behind it; only a cold miss blocks |
| ~~Cursor pagination semantics~~ | ~~M1~~ | **Settled — [ADR 0003](decisions/0003-no-cursor-pagination.md).** No cursor; truncation is terminal |
| Affected-row pre-check form | M3 | Parse-tree predicate check, never `EXPLAIN` (`CLAUDE.md` open question) |
| Before-image size ceiling | M3 | Truncate-with-marker (`CLAUDE.md` open question) |
| Approval default for schema changes | M3/M4 | Stays per-source — destructive DDL is refused structurally ([ADR 0002](decisions/0002-ddl-additive-and-corrective.md)) |
| Table-rewrite cost estimation for DDL | M3 | Probably not; rely on `lock_timeout` + `statement_timeout` ([ADR 0002](decisions/0002-ddl-additive-and-corrective.md)) |
| Approval-provider plugin mechanism | M4 | Generic webhook provider in core; Slack built against it |
| Identity beyond static tokens | post-M5 | Defer (`CLAUDE.md` open question) |
