# Changelog

All notable changes to RTFQ are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RTFQ uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

**Before 1.0, treat minor versions as potentially breaking.** The wire contract — the response envelope, the
`domain.reason` error codes, and the caps and timeouts — is API surface that agents branch on, so changes to it
are called out under *Changed* rather than buried in *Fixed*.

## [Unreleased]

Nothing yet. Next up is M5: the Docker image, a quickstart timed on a clean machine, and the security posture
document written for whoever has to approve pointing this at production.

## [0.7.1] - 2026-08-21

Field-reported: an approval-required source left an agent looping on `pending` with no reachable way to approve.

### Added

- **`rtfq approvals --watch`** stays open and reports requests as they arrive, prompting `[a/d/s]` when there is
  a person at the keyboard and printing the commands when there is not. This is the answer to "nobody was
  notified" for a single operator; a webhook provider remains the answer for a team. Skipping is a first-class
  answer and the default for anything unrecognised, because the safe direction is always "not yet".

### Fixed

- **A proposal awaiting approval now says who has to do what.** It carries the `approval_id`, and the hint names
  the exact command — `rtfq approvals --approve <id> --as NAME` — and states plainly that **nobody has been
  notified**. The previous wording, "a human has been asked to approve this exact diff", was wrong twice over:
  nobody had been asked, and the caller had no id to quote, so a change could only sit until it lapsed. Every
  `pending` response repeats it, since an agent polling is the one being asked "so what do I do?".
- **`commit_write`'s description never mentioned approval**, so an agent was told to confirm the diff and commit
  while the runtime did something else. It now says the call answers `pending` until a person approves it outside
  the session, that the agent cannot approve it itself, and not to poll in a loop. `propose_write` says the same.
  Reported from real use; it is the defect class CLAUDE.md added a working agreement about two releases ago.

Next up is M5: the Docker image, a quickstart timed on a clean machine, and the security posture
document written for whoever has to approve pointing this at production.

## [0.7.0] - 2026-08-18

### Added

- **Four MCP prompts** - `diagnose_slow_query`, `explore_source`, `investigate_record` and `propose_fix`. These
  are procedures, not capabilities: each is carried out through the existing tools, so everything they lead to
  passes the same gates, and none asserts anything about your data.

  Prompts rather than a tenth tool, deliberately. A tool's description sits in the model's context all session -
  which is why CLAUDE.md says adding one needs an argument - while a prompt is listed by name and its body only
  fetched when it is run. The server now declares the `prompts` capability and answers `prompts/list` and
  `prompts/get`.

- **A failed read now says what to do about it.** A timeout points at `explain` on the same statement, because
  the plan is what distinguishes a missing index from a query that was always going to read the table, and an
  agent that only learns "too slow" retries a variant and is slow again. A dialect error points at
  `describe_table`, which answers a column-name question from cache. Carried in the error's `detail` rather than
  folded into the message, so a client can show or suppress it without parsing prose.

- A truncated `query` response now also names `explain`, alongside what it already said about narrowing.
  Nothing is added to responses that went fine.

### Fixed

- **CI reported success on a publish that failed.** `docker run ... | tee publish.log` returned `tee`'s exit
  code, so an AOT trim error passed the publish step; the two steps after it then ran against a binary that had
  never been produced and passed as well, one of them printing `highest glibc symbol required:` with nothing
  after it. The pipeline now sets `pipefail`, the glibc check asserts the binary exists and that objdump found
  symbols, and a trim or AOT analysis error in RTFQ's own code is caught and named rather than surfacing as a
  linker exit code.

## [0.6.0] - 2026-08-18

### Added

- **`writable_tables` accepts patterns.** `dbo.*` covers a schema; `*` covers everything. Previously the
  allow-list was exact-match only, and the answer to "make this dev schema writable" was a hundred lines of
  YAML — which is the kind of friction that ends with somebody removing the gate entirely. See
  [ADR 0008](docs/decisions/0008-wildcards-in-the-write-allow-list.md).

  Three things did not move: `deny_tables` is still evaluated first and still wins, an empty allow-list still
  means nothing is writable, and matching is still ordinal and case-sensitive. An entry with no `*` matches
  exactly as before, so existing configs are unaffected.

  **The cost, stated plainly:** a pattern covers tables that do not exist yet, so `dbo.*` includes whatever is
  created next month. `rtfq validate` now warns per pattern (`source.writable_wildcard`) rather than letting a
  gate get wider quietly, and on anything that matters `require_approval` moves from advisable to load-bearing.

- **`scripts/windows/`** — PATH, environment, self-signed certificate and firewall rule, one script each. They
  came out of a real deployment rather than being written to look tidy, so the comments carry the traps: the
  certificate script needs PowerShell 7 because `ExportPkcs8PrivateKey` does not exist in 5.1, a Windows service
  cannot see your user environment, and a SAN must name what callers actually dial. `scripts/README.md` collects
  those in one place.
- **`examples/rtfq.mssql.yaml`** — several SQL Server databases plus a PostgreSQL, the shape of a typical
  internal estate, including two databases that share a host and are still two sources.

### Documentation

- `CLAUDE.md` gains a working agreement: **the API must never lie to an agent about what it can do.** A stale
  hint or a hard-coded capability flag is a defect of the same order as a broken gate, because an agent plans
  from what discovery tells it. M4 shipped three, and all three passed the suite.
- `CLAUDE.md` states the one exception to *secrets are referenced*: `server.tls.cert` and `server.tls.key` take
  paths, and a diagnostic must never echo the value when they do not.

Next up is M5: the Docker image, a quickstart timed on a clean machine, and the security posture document
written for whoever has to approve pointing this at production.

## [0.5.0] - 2026-08-18

**M4 — approval and the time-boxed unlock**, the control `CLAUDE.md` calls the central defence against a
well-formed malicious write. See [ADR 0007](docs/decisions/0007-m4-approval-and-unlock.md).

### Added

- **Human approval on a commit.** A source with `require_approval: true` now queues its proposal for a person
  instead of refusing it. The approver sees the statement and the affected rows as they are now, and nothing
  else — there is no field in which an agent can supply a summary, because the case this gate exists for is an
  agent persuaded by a poisoned row.
- **`rtfq approvals`** lists what is waiting, with `--approve ID --as NAME` and `--deny ID --as NAME` to answer it.
  The decision and the approver land in the audit log beside the exact statement.
- **`rtfq unlock SOURCE --write --ttl 15m`** and **`rtfq lock SOURCE`**. With `require_unlock: true` a source is
  shut at runtime even where the config permits writing, until somebody deliberately opens it. TTL is clamped to
  an hour, expiry is evaluated on read rather than swept, and **a restart re-locks** — not configurable.
- **A webhook approval provider**, selected with `approval: {mode: webhook, endpoint: ...}`. This is how a Slack
  integration is built without Slack living in core: NativeAOT rules out loading plugin assemblies, so the
  boundary is HTTP. Anything the endpoint says that is not a recognised verdict — a 500, a timeout, malformed
  JSON, an unreachable host — is treated as *not yet decided*, never as approval.
- `approval_ttl` in `defaults:` (10m), and the `approval:` config section, both validated by `rtfq validate`.

### Changed

- **An approval-required proposal holds no transaction open while a human decides.** It runs, captures the diff,
  and rolls straight back. Commit re-runs the statement and refuses unless the diff is identical to the one that
  was approved. Holding a transaction across a human's attention span blocks readers on SQL Server and holds back
  `VACUUM` on PostgreSQL; a gate that expensive gets switched off.
- `describe_table` now reports **`writable` truthfully** — the intersection of source access, token grant and the
  write allow-list. It had been hard-coded to `false` since before M3, telling agents they could not write to
  tables they could.
- The MCP hint on an approval-required proposal explained that commit would be refused "until an approver exists
  (M4)". It now says nothing is held, that `commit_write` answers `pending` until somebody decides, and when the
  request lapses — the old text would have an agent abort instead of wait.
- An HTTP source refusing a non-GET no longer says writes "arrive in M3". HTTP sources are read-only by design:
  the write path needs a transaction that can capture before-images and roll back, and a request that has been
  sent cannot be un-sent.

### Security

- An approval binds to **one change, not a shape of change**. The fingerprint covers the statement, the affected
  count and the before-images, so if the rows move while somebody is deciding, the commit is refused and rolled
  back rather than applied to data nobody approved.
- Seeing or answering the approval queue requires a token with write access to some source. A read-only agent has
  no business in the queue that exists to police it.
- Opening a source requires the access being opened, and an unlock for `write` does not open `schema`.
- `server.tls.cert` and `server.tls.key` take a **path**, unlike every other secret in the file. Writing
  `${file:...}` there substituted the PEM and then reported it as a missing filename — printing a private key
  into the terminal, and into whatever CI log or screenshot the error landed in. That case is now named rather
  than echoed, and any over-long value in the diagnostic is truncated.

### Changed — configuration

- The sample configs renamed their token secrets: `RTFQ_TOKEN_AGENT` is now **`RTFQ_AGENT_SECRET`** in
  `rtfq.dev.yaml`, and `RTFQ_READONLY_SECRET` / `RTFQ_WRITER_SECRET` in `rtfq.multi.yaml`. The old names sat one
  character away from `RTFQ_TOKEN`, the client's own variable, while meaning something entirely different — one
  is a secret the server's config resolves, the other is the bearer token a client presents. **These are names
  the sample chooses, not names RTFQ knows**, so this affects nobody's deployment: if your config says
  `${env:RTFQ_TOKEN_AGENT}`, it still works.

### Documentation

- `CLAUDE.md`'s illustrative config wrote TLS as `cert: ${file:/etc/rtfq/tls.crt}`, which is exactly the form
  that does not work. Corrected to paths, and the snippet now passes `rtfq validate --production` as written.
- The README covers **putting `rtfq` on your PATH** on each platform, and **reaching the server from another
  machine**: certificate, config, firewall, and the reasons not to point the port at the public internet. Every
  command in both sections was run before it was written down.

## [0.4.0] - 2026-08-17

**M3 — the write path**, for PostgreSQL and SQL Server. See
[ADR 0006](docs/decisions/0006-m3-write-path.md).

### Added

- **`propose_write`, `commit_write`, `abort_write`** on the HTTP API and as MCP tools. A proposal runs the
  statement inside a transaction and stops: it reports the real number of rows changed and the rows as they were
  beforehand, and nothing is saved until it is committed.
- **The four structural gates** — source access, token grant, target allow-list, statement guard — each with its
  own refusal code. `writable_tables`, `deny_tables`, `max_affected_rows` and `require_approval` per source.
- **Before-image journaling.** The affected rows are captured inside the same transaction, at repeatable read so
  the capture and the mutation see the same rows, and written to the audit log at propose time.
- **Additive schema changes** per [ADR 0002](docs/decisions/0002-ddl-additive-and-corrective.md), gated at the
  subcommand level and requiring `access: schema`.

### Security

- The affected-row cap is enforced against the driver's **real count from the uncommitted execution**, never an
  estimate. One row over rolls back and refuses with the count.
- Unqualified `UPDATE`/`DELETE` are refused, and so are trivially-true predicates (`WHERE 1=1`, `WHERE true`,
  `… OR true`) — a `WHERE`-presence test is not enough.
- Deny rules apply to everything a statement touches, so a write to an allowed table that reads a denied one
  through a subquery is refused.
- Handles are single-use, owned by their creator, capped at four open per source, and roll back on abort, on
  expiry, and on shutdown.
- A write nested inside another statement — a `DELETE` in a CTE — is refused: it has no unambiguous target, so
  the allow-list and the cap would be guessing.

### Known gaps

- **MongoDB and HTTP writes are refused.** Mongo needs a replica set and its own adversarial suite; HTTP has no
  transaction to leave open.
- **`require_approval` refuses the commit** rather than queuing it. There is no approver until M4.
- **On SQL Server an open proposal blocks readers** of the affected rows, where PostgreSQL shows them the
  pre-image. Enable `READ_COMMITTED_SNAPSHOT`, and treat the handle TTL as an availability control there.
- The unbounded-statement pre-check is still not implemented; an uncommitted runaway does its work before being
  rolled back, bounded only by `statement_timeout` and `lock_timeout`.

## [0.3.0] - 2026-08-17

**M2 — SQL Server, MongoDB and HTTP adapters.** The interface held; see
[ADR 0005](docs/decisions/0005-m2-interface-audit.md).

### Added

- **SQL Server** (`kind: mssql`) via `Microsoft.Data.SqlClient`, with a read guard on Microsoft's own ScriptDom,
  catalog introspection including row estimates and indexes, and `SHOWPLAN_ALL` for `explain`.
- **MongoDB** (`kind: mongodb`). Statements are command documents — `{"find": "orders", "filter": {…}}` — which is
  Mongo's own dialect. Schema is **inferred** from sampled documents and says so, reporting every observed type
  for a field (`total double|int32`) rather than picking one.
- **HTTP APIs** (`kind: http`). Statements are request lines: `GET /v1/invoices?status=open`. Paths must be
  explicitly allow-listed; an empty allow-list reaches nothing rather than everything.
- **A shared adapter conformance suite** every adapter runs unmodified against a real containerised instance.
- **Startup capability checks.** Some declarations can only be tested against a live source, so `rtfq validate`
  stays offline and the server verifies at boot. A source that cannot support its declared access stops startup;
  a source that is merely unreachable is a warning.

### Changed

- `ISourceAdapter.CheckAsync` returns capabilities instead of void, because MongoDB's transaction support depends
  on deployment topology and cannot be known at construction.
- Which kinds exist and what they can do moved into `AdapterCatalog`; config validation asks the adapter layer
  rather than keeping its own list. This was the one leak the interface audit found.

### Security

- **Guards for all three new dialects**, each covering its own version of "not a write command, but catastrophic":
  T-SQL `EXEC`/`sp_executesql`/`xp_cmdshell` (dynamic SQL the parse tree cannot see into), Mongo's `$out` and
  `$merge` (aggregation *stages* that write a collection) and `$where`/`$function` (server-side JavaScript), and
  for HTTP a wildcard path combined with a write method, which is now a config error.
- All guards are **verified in the published AOT binary** against real servers, not only under the JIT.

### Known gaps

- ⚠ **Linux hosts now need `libicu` installed.** `Microsoft.Data.SqlClient` refuses to run in invariant
  globalization mode, so that setting had to be turned off. Windows and macOS are unaffected.
- Linux artifacts now target **glibc 2.34**, so Debian 12, Ubuntu 22.04 and RHEL 9 are supported. M2's new
  dependencies had briefly raised the floor to 2.38 — enough to exclude all three — because a binary links
  against whatever glibc built it. Linux builds now happen inside an Ubuntu 22.04 container and CI asserts the
  ceiling, since this is the kind of thing that drifts upward without anyone noticing.
- The binary is now ~66 MB, up from 20 MB.
- Still reads only; the write path is M3.

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

[Unreleased]: https://github.com/a7ex-turcan/rtfq/compare/v0.7.1...HEAD
[0.7.1]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.7.1
[0.7.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.7.0
[0.6.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.6.0
[0.5.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.5.0
[0.4.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.4.0
[0.3.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.3.0
[0.2.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.2.0
[0.1.0]: https://github.com/a7ex-turcan/rtfq/releases/tag/v0.1.0
