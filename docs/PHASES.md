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

1. **Toolchain and repo skeleton.** Go is not installed on the dev box yet — that is step one. Pin the toolchain
   in `go.mod`, add `Makefile`/`Taskfile`, `golangci-lint`, and CI (build + vet + lint + test on push).
2. **Package layout** (see [below](#package-layout)). Create the directories with their interfaces, even where
   the only implementation is Postgres.
3. **Config loader** — YAML → typed struct. Secret resolution for `${env:...}` and `${file:...}`
   (`${vault:...}` is a stub returning "unsupported" until there is demand). Plaintext secret ⇒ warning in dev,
   hard fail in production mode.
4. **Config validation as a first-class pass**, separate from loading, with `rtfq validate --config rtfq.yaml`.
   Every "this is a config validation error, not a warning" rule in `CLAUDE.md` gets registered here as a named
   check, even the ones whose subject doesn't exist yet (Mongo-standalone, HTTP wildcard + write). An empty
   check that is wired up is cheap; a validation framework retrofitted in M2 is not.
5. **Wire contract** — `internal/api`: request/response DTOs, the response envelope
   (`row_count`, `truncated`, `elapsed_ms`, `next_cursor`), and the **error taxonomy**.
6. **Transport** — HTTP+JSON over TLS, token auth middleware (constant-time compare), request IDs, structured
   logging. **TLS is mandatory unless the listener is loopback** — a rule, not a config knob.
7. **Policy engine, thin but real** — token grant × source access → effective permission, default-deny.
   Target allow-lists and the mutation gates arrive in M3; the *shape* (an intersection, evaluated in one place)
   is fixed now.
8. **Adapter interface + Postgres adapter** — `connect`, `introspect`, `sample`, `execute-read`, plus a
   `Capabilities()` declaration. Write-side methods exist on the interface and return "unsupported" until M3.
   `pgx` for the driver.
9. **Row cap and statement timeout.** M0 enforces the cap by **stopping the row scan** at `max_rows + 1` and
   setting `truncated: true` — real LIMIT injection needs the parser and lands in M1. `statement_timeout` is set
   on the connection from day one; per `CLAUDE.md` it is a blast-radius guard, not a nicety.
10. **Audit log, minimal.** Append-only JSONL: caller, source, statement, classification, outcome, duration.
    Before-images land in M3. *This is an addition to M0 as written in `CLAUDE.md`* — deliberate, because an
    audit sink threaded through the request path afterwards is a rewrite, and "every request is audited" is only
    true if it was never optional.
11. **CLI** — `rtfq serve`, `rtfq query`, `rtfq sources`, `rtfq validate`. Thin client over `internal/client`.
12. **Test harness** — testcontainers-go against real Postgres. No mocks for adapters, per the working agreements.

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
- ✅ Every truncated response is machine-readably truncated, with a usable `next_cursor`.
- ✅ Go/no-go review held, written down. If the surface is unpleasant, the fix is a redesign here — not a workaround in M2.

### Risks and decisions

- ❓ **Cursor pagination semantics — genuinely undecided, and not yet in `CLAUDE.md`'s open questions.**
  Arbitrary caller-authored SQL cannot be keyset-paginated generically. The options:
  **(a)** re-execute with `OFFSET` — stateless and simple, but non-snapshot: rows can shift between pages;
  **(b)** hold a server-side cursor in an open transaction with a TTL — consistent, but costs a held connection
  and a resource-leak surface;
  **(c)** refuse to paginate and make truncation terminal — the caller narrows the query instead.
  Lean **(a) with an explicit `snapshot: false` marker in the envelope**, because silence about inconsistency is
  the actual sin, and (b) reintroduces the held-resource problem the write path already has. Decide before the
  envelope is frozen.
- ⚠ **Compactness is a design problem, not a formatting problem.** If `describe_source` is just a table dump with
  fewer columns, this phase has failed and M2–M5 inherit the failure.

---

## Phase M2 — Adapters: SQL Server, MongoDB, HTTP

**Goal:** prove the adapter interface holds. The features are a side effect; the interface validation is the point.

### Scope

1. ~~**Parser spike (do this first — it gates M3).**~~ **Done — [ADR 0001](decisions/0001-sql-parser-selection.md).**
   PostgreSQL uses `wasilibs/go-pgquery` (libpg_query compiled to wasm), SQL Server uses `sqlc-dev/teesql`
   (pure Go, ScriptDom-shaped AST). Both run with `CGO_ENABLED=0`, so the single-static-binary requirement and
   the real-parser requirement do not conflict after all, and M5's release pipeline stays simple.
2. **SQL Server adapter** — `go-mssqldb`; introspection, read, `SET SHOWPLAN_XML` for `explain`.
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

### Exit criteria

- ✅ All four adapters pass a shared conformance suite (connect, introspect, sample, read, cap, timeout, unreachable)
  against real containerized instances.
- ✅ The interface audit lists **zero** dialect-specific branches above `internal/adapter/`. Any that exist are
  written up as interface bugs with a fix, per the working agreements.
- ✅ Config validation rejects: Mongo-standalone marked writable; HTTP wildcard + `POST`.
- ✅ `describe_*` output for Mongo marks inferred schema as inferred, with the sample size it was inferred from.

### Risks and decisions

- ✔ **Parser choice per dialect — resolved by [ADR 0001](decisions/0001-sql-parser-selection.md)**, and it
  resolved better than expected: `go-pgquery` runs PostgreSQL's *own* parser compiled to WebAssembly on a pure-Go
  runtime, so there is no correctness-versus-build-simplicity trade to make. Adversarial corpus: 35/35 Postgres,
  19/19 T-SQL, five cross-compile targets, no C toolchain. Residual risk moved to the dependency itself —
  `teesql` is young and lenient, so pin it, vendor it, and keep the corpus as a regression suite.
- ⚠ **The guard is an allow-list, not a DDL deny-list** — an ADR 0001 finding that changes this phase's shape.
  `COPY ... FROM PROGRAM`, `DO`, `EXPLAIN ANALYZE`, `GRANT`, `SET ROLE` and `EXEC xp_cmdshell` are none of them
  DDL, and all of them are catastrophic. Execute only the enumerated node types; refuse everything else by default.
- ⚠ **Mongo's "statement" is not a string.** If the guard interface assumes SQL text, Mongo will bend the core —
  exactly the leak this phase exists to catch. Classification belongs to the adapter; the core sees a
  *classification result*, not a dialect.

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
   `search_path` games, and a mutation that exceeds the cap by exactly one row.

### Not in this phase

Human approval (M4) — M3 ships with `require_approval` parsed and enforced as *"reject the commit"*, not as
"prompt someone". Auto-commit sources work; approval-required sources cannot yet be approved. That keeps M3's
test surface structural.

### Exit criteria

- ✅ The adversarial suite passes on every write-capable adapter, and a reviewer other than the author agrees it
  is convincing. This is the ship gate, per `CLAUDE.md`.
- ✅ A mutation one row over the cap is refused with the real count, and the transaction is provably rolled back
  (verified by re-reading the rows).
- ✅ An expired handle rolls back automatically; a handle cannot be re-pointed at a different statement.
- ✅ Every mutation has before-images in the audit log, traceable to the read that preceded it.
- ✅ Server killed mid-transaction leaves no committed partial write.

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

- ❓ **What "plugin" means in Go.** Go has no comfortable dynamic plugin story. Realistic options for Slack:
  a generic **webhook provider in core** that an external Slack bot implements (keeps core clean, one extra hop),
  or a **build-tagged optional import** (one binary, but Slack code ships in the repo). Lean webhook — it keeps the
  `CLAUDE.md` promise that Slack is never in core, and any provider can be built against it.
- ⚠ **Approval fatigue is a real failure mode.** If approving is tedious, people will set `require_approval: false`
  and the intent gate is gone. The reference implementation's ergonomics are a security property.

---

## Phase M5 — Shipping

**Goal:** a stranger with a Postgres and five minutes gets to a first query — and a security reviewer can approve
pointing this at production.

### Scope

1. **Release pipeline** — reproducible single-binary builds for linux/darwin/windows × amd64/arm64. Constrained by
   the M2 parser decision if it forced cgo.
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
- ✅ Release artifacts verified on each target platform.
- ✅ Security posture doc reviewed by someone who did not write the code.
- ✅ README carries the honest one-paragraph version of the threat model, not a marketing paragraph.

---

## Package layout

Provisional, established in M0 so later phases have somewhere to land:

```
cmd/rtfq/              # single binary: serve, query, sources, describe, validate, refresh, unlock, mcp
internal/
  api/                 # wire DTOs, response envelope, error taxonomy   (the stable contract)
  config/              # load, secret resolution, validation checks
  server/              # HTTP+JSON, TLS, auth middleware, handlers
  policy/              # grant x access x allow-list -> effective permission
  guard/               # classification results; per-dialect parsing lives in adapters
  broker/              # transactions, handles, TTL, caps, approval hookup
  audit/               # append-only JSONL, before-images
  schema/              # introspection model, cache, staleness
  adapter/             # Adapter interface + capabilities
    postgres/ mssql/ mongo/ http/
  client/              # HTTP client shared by CLI and MCP
  mcp/                 # MCP tool definitions -> client calls (thin, no logic)
docs/
```

Everything above `adapter/` is source-agnostic. If that stops being true, the interface is wrong.

---

## Cross-cutting, owned from M0

These are not phases; they are properties every phase maintains.

| Concern | Rule |
|---|---|
| **Error taxonomy** | Stable machine-readable codes. Agents branch on them, so they are API surface. |
| **Response envelope** | `row_count`, `truncated`, `elapsed_ms`, `next_cursor`, staleness. Silent truncation is a bug. |
| **Audit** | Every request, including refusals. Never optional, never shipped off the box. |
| **Tests** | Real containerized instances per adapter. Read-only-enforcement and mutation-guard suites are adversarial. |
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
| ~~Parser per dialect (cgo trade-off)~~ | ~~M2, gates M3~~ | **Settled — [ADR 0001](decisions/0001-sql-parser-selection.md).** wasm libpg_query + teesql, both `CGO_ENABLED=0` |
| Function-call deny-list contents | M3 | Cover the known-dangerous set; the scoped `GRANT` is the real boundary ([ADR 0001](decisions/0001-sql-parser-selection.md)) |
| Proactive vs. serve-stale-then-refresh | M1 | Whichever keeps `describe_*` fast (`CLAUDE.md` open question) |
| Affected-row pre-check form | M3 | Parse-tree predicate check, never `EXPLAIN` (`CLAUDE.md` open question) |
| Before-image size ceiling | M3 | Truncate-with-marker (`CLAUDE.md` open question) |
| Approval-provider plugin mechanism | M4 | Generic webhook provider in core; Slack built against it |
| Identity beyond static tokens | post-M5 | Defer (`CLAUDE.md` open question) |
