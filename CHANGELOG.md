# Changelog

All notable changes to RTFQ are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RTFQ uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Before 1.0, treat minor versions as potentially breaking.** The wire contract — the response envelope, the
`domain.reason` error codes, and the caps and timeouts — is API surface that agents branch on, so changes to it
are called out under *Changed* rather than buried in *Fixed*.

## [Unreleased]

Nothing yet. Next up is M2: SQL Server, MongoDB and HTTP adapters.

## [0.2.0] - 2026-08-15

**M1 — the MCP read surface and the schema cache.** The go/no-go milestone, and the verdict was GO:
[ADR 0004](docs/decisions/0004-m1-go-no-go.md) records the evidence.

### Added

- **`rtfq mcp`** — an MCP server over stdio exposing six tools: `list_sources`, `describe_source`,
  `describe_table`, `sample`, `query` and `explain`. A thin adapter over the same HTTP API the CLI uses; it holds
  no policy, so talking to the port directly cannot bypass anything.
- **Schema introspection and an on-disk cache.** Columns, types, nullability, defaults, primary keys, indexes and
  foreign keys, with planner row estimates rather than `COUNT(*)`.
- **Discovery survives an unreachable source.** `describe_source` and `describe_table` keep answering from cache
  when the database is down, and every response states how old the answer is. An agent can draft a correct
  statement offline and only needs the source live to run it.
- **`describe`, `refresh`, `explain` and `mcp` CLI commands**, and `explain`/`sample`/`describe_*` on the HTTP API.
- **A measured token budget.** Discovery output is asserted in CI and printed on every run: `describe_source` on
  a 202-table database costs ~379 tokens against a 1500 ceiling.

### Changed

- **`query` now injects a `LIMIT` via the parse tree** instead of stopping the row scan, so the planner can
  optimise for the cap. The scan-stop remains as a backstop, and the injected limit is `cap + 1` so that
  "exactly full" stays distinguishable from "clipped".
- **Truncation is terminal and says so.** `next_cursor` is now permanently null and a truncated response carries
  a `hint` explaining how to narrow, per [ADR 0003](docs/decisions/0003-no-cursor-pagination.md). This reverses
  the earlier plan to paginate by offset, which would have cost a full scan per page against production.
- `QueryResponse` gains `hint`; discovery responses are new. Additive, so existing clients are unaffected.

### Security

- **A read-granted token can no longer write.** In 0.1.0, policy checked the *caller* and nothing checked the
  *statement*, so an `UPDATE` sent to `query` would run if the database credential permitted it — only the
  `GRANT` stood in the way. The read guard now parses every statement and refuses anything that is not a plain
  read, including writes hidden inside a CTE, `SELECT INTO`, `COPY ... FROM PROGRAM`, `DO` blocks and
  `EXPLAIN ANALYZE`. If you ran 0.1.0 against a database whose credential could write, assume that was reachable.

### Known gaps

- Still reads only; the write path is M3.
- PostgreSQL only; other adapters are M2.
- `describe_source` clips its table list at 80, so discovery on very large estates depends on filtering.
- The AOT artifact is **no longer a single file**: the parser is a native library, so bundles now contain the
  binary plus `libpg_query.so`. Foreseen in [ADR 0001](docs/decisions/0001-sql-parser-selection.md).

## [0.1.0] - 2026-08-14

First release. **M0, the walking skeleton**: one query travels from the CLI to PostgreSQL and back over the real
wire protocol, capped and audited. Thin, but nothing in it is a placeholder.

### Added

- **`rtfq serve`** — HTTP+JSON over TLS with bearer-token authentication. The stable contract that the CLI, and
  from M1 the MCP adapter, are both clients of.
- **`rtfq query`**, **`rtfq sources`**, **`rtfq validate`** — a thin client over that same API. Nothing the CLI
  can do bypasses a rule the server enforces.
- **PostgreSQL source adapter** over Npgsql, with a pinned `search_path`, a server-side `statement_timeout`, and
  schema introspection.
- **Policy engine** — default-deny. Effective access is the intersection of what the source declares and what the
  token was granted, so enabling anything beyond reads takes two edits in two places.
- **Row caps as contract** — every response carries `row_count`, `truncated` and `elapsed_ms`. A caller may lower
  its own cap and can never raise it.
- **Config validation as a separate pass**, with named checks and line numbers, so `rtfq validate` can answer
  "is this safe to run?" without opening a listener or a connection.
- **Secret references** — `${env:...}` and `${file:...}`. An unresolvable reference is refused rather than
  defaulting to empty.
- **Audit log** — append-only JSONL covering every request *including refusals*, recording caller, source,
  statement, outcome and error code. It stays on the box.
- **Stable error taxonomy** in `domain.reason` form (`policy.source_unknown`, `source.rejected`, …).
- **NativeAOT single-file binaries** for linux-x64, linux-arm64, win-x64 and osx-arm64.

### Security

- **TLS is required unless the listener is loopback.** A rule, not a config knob; there is no `--insecure` escape
  hatch on the server.
- **An ungranted source is indistinguishable from one that does not exist.** Both return the same code and the
  same message, so an unauthorised token cannot enumerate the estate.
- **Inline secrets are a hard failure under `--production`** and a warning in development.
- **Token comparison is constant-time**, and every configured token is compared on every attempt, so neither the
  number of tokens nor the position of a match is observable in the response time.

### Known gaps

- **Reads only.** The write path is M3; `access: write` and `access: schema` are accepted by config validation but
  nothing yet acts on them.
- **No MCP surface yet** — that is M1, and it is the go/no-go milestone.
- **PostgreSQL only.** SQL Server, MongoDB and HTTP sources arrive in M2, and a config naming them is refused at
  load rather than half-supported.
- **No cursor pagination.** `next_cursor` is present in the envelope and always null; over-cap results are
  truncated and flagged.
- **No osx-x64 build.** Intel Macs are deliberately unsupported: the PostgreSQL parser RTFQ adopts in M3 ships no
  osx-x64 native payload, so shipping the artifact now would mean withdrawing it later. See
  [ADR 0001](docs/decisions/0001-sql-parser-selection.md). No win-arm64 build either.

[Unreleased]: https://github.com/a7ex-turcan/rtfq/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.2.0
[0.1.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.1.0
