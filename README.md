# RTFQ — *Run The F\*\*\*ing Query*

A single self-hosted binary that gives AI agents and developers **governed, schema-aware, auditable access to
heterogeneous data sources** on a machine they cannot otherwise reach.

One binary, one YAML file, five minutes to first query. **Read by default, write on a leash.**

> **Status: M4 — approval and unlock.** PostgreSQL, SQL Server, MongoDB and allow-listed HTTP APIs over an
> authenticated TLS port, capped and audited; schema discovery that keeps working when the database does not; and
> a write path where a change is proposed, shown to a human as a statement and a diff, and saved only once
> somebody says yes. What remains is M5: packaging and the quickstart. See [docs/PHASES.md](docs/PHASES.md).

---

## Why

Letting an agent answer *"why is this order stuck?"* — let alone fix it — currently means handing it a
production connection string, standing up one MCP server per database, or buying an enterprise access platform.
RTFQ is the option for the eight-person team: one binary that holds the config, the credentials, the policy, and
the audit log, and exposes a port that agents and humans both talk to.

## Install

Grab a bundle from [Releases](https://github.com/a7ex-turcan/rtfq/releases). Each one contains a single
self-contained binary — no .NET runtime to install — plus this README, the licence, the changelog, and a sample
config.

| Platform | Artifact |
|---|---|
| Linux x64 | `rtfq-<version>-linux-x64.tar.gz` |
| Linux arm64 | `rtfq-<version>-linux-arm64.tar.gz` |
| Windows x64 | `rtfq-<version>-win-x64.zip` |
| macOS Apple Silicon | `rtfq-<version>-osx-arm64.tar.gz` |

```bash
tar -xzf rtfq-0.1.0-linux-x64.tar.gz && cd rtfq-0.1.0-linux-x64
./rtfq --version
```

Verify against `SHA256SUMS`, published with each release. Intel Macs and Windows on ARM are not built — see
[CHANGELOG.md](CHANGELOG.md) for why that is a decision rather than an oversight.

**Linux prerequisites.** The bundle needs `libicu` on the host — `apt install libicu72` or
`dnf install libicu`. Most server images already have it; minimal containers do not. This is not optional:
the SQL Server driver refuses to run without globalization support, so the binary aborts at the first query
rather than degrading. Windows and macOS need nothing extra.

Linux builds target **glibc 2.34**, so Debian 12, Ubuntu 22.04 and RHEL 9 are all supported. That floor is
asserted in CI, because it is set by whichever host compiles the binary and drifts upward silently.

## Try it

```bash
export RTFQ_TOKEN_AGENT='dev-token-please-change'
export ORDERS_DSN='Host=localhost;Port=5432;Database=orders;Username=rtfq'

rtfq validate --config examples/rtfq.dev.yaml
rtfq serve    --config examples/rtfq.dev.yaml
```

Then, from anywhere that can reach it:

```bash
export RTFQ_SERVER='http://127.0.0.1:7420'
export RTFQ_TOKEN='dev-token-please-change'

rtfq sources
rtfq describe --source orders                        # what tables exist
rtfq describe --source orders --table public.orders  # columns, keys, indexes
rtfq query --source orders "SELECT id, customer, total FROM orders WHERE vip" --max-rows 20
```

```
id   customer      total
---  ------------  ------
7    customer-7    10.50
14   customer-14   21.00

2 rows, 4 ms
```

## When the agent wants to change something

The sample config has the orders source configured for writes, and shut. Writing is a three-step conversation, and
you are in it:

```bash
rtfq unlock orders --write --ttl 15m   # opens it, for fifteen minutes; a restart re-locks
```

The agent proposes a change. It runs inside a transaction, reports the real number of rows it would touch, and is
rolled back — nothing is saved. Because this source sets `require_approval`, it goes to you:

```bash
rtfq approvals
```

```
------------------------------------------------------------------------
91d09ef04b54  mutation on orders/public.orders  (1 row(s))
requested by token 'agent', expires 2026-08-18T07:17:10Z

  statement:
    UPDATE orders SET status = 'paid' WHERE id = 1

  rows as they are now:
    id | status
    1  | stuck

  approve: rtfq approvals --approve 91d09ef04b54 --as YOU
  deny:    rtfq approvals --deny 91d09ef04b54 --as YOU
------------------------------------------------------------------------
```

You see the statement and the rows. You never see a summary the agent wrote, because the case this gate exists for
is an agent that has been persuaded by something it read, and such an agent writes a very reassuring summary.

Answering it lets the agent's next `commit_write` through — and if the rows moved while you were deciding, the
commit is refused rather than applied to data you did not approve. `rtfq lock orders` shuts it again.

## Point an agent at it

```bash
rtfq mcp    # speaks MCP on stdio; RTFQ_SERVER and RTFQ_TOKEN select the server
```

Nine tools: `list_sources`, `describe_source`, `describe_table`, `sample`, `query`, `explain`, `propose_write`,
`commit_write`, `abort_write`. Meeting an unfamiliar four-table database and answering a question that needs a
join costs about **240 tokens end to end**; `describe_source` on a 202-table database costs about **379**. Those
numbers are asserted in CI and printed on every run, because an agent pays them on every call — see
[ADR 0004](docs/decisions/0004-m1-go-no-go.md).

## What it enforces, today

- **Default deny.** A caller sees only sources it was granted, and a source it cannot reach is reported exactly
  like one that does not exist — so an unauthorised token cannot enumerate your estate.
- **Permission is an intersection.** Effective access is the lower of what the source declares and what the token
  was granted, so enabling anything beyond reads always takes two edits in two places.
- **Row caps are a contract.** Every response carries `row_count`, `truncated` and `elapsed_ms`. A caller may
  lower its own cap and can never raise it. Silent truncation is a bug, not a nuance.
- **TLS unless loopback.** The moment the listener is reachable from another machine, a certificate is required.
  That is a rule, not a config knob, and there is no `--insecure` escape hatch on the server.
- **Secrets are referenced, never inlined.** `${env:...}` and `${file:...}`. A password written into the config
  is a warning in development and a hard failure under `--production`.
- **Reads are reads.** Every statement is parsed before it runs — a real parser, per dialect, never a regex — and
  a read that is not a read is refused: a write hidden inside a CTE, `SELECT INTO`, `COPY ... FROM PROGRAM`,
  `EXPLAIN ANALYZE`.
- **Nothing is written by accident.** A mutation is proposed and committed as two steps. The proposal runs inside
  a transaction, reports the driver's **real** affected-row count, captures the rows as they were, and saves
  nothing. An unqualified `UPDATE` or `DELETE` is refused, and so is a trivially-true `WHERE`.
- **Writable is an allow-list, per table.** An absent allow-list reaches nothing rather than everything, and a
  deny rule beats it — including for what a statement merely reads through a subquery.
- **A human can be required, and sees only facts.** `require_approval` puts the statement and the affected rows in
  front of a person. The approval binds to that exact change: if the data moves while they decide, the commit is
  refused and rolled back.
- **Writing can be shut even where it is configured.** `require_unlock` keeps a source closed until somebody runs
  `rtfq unlock`. The window is capped at an hour and **a restart re-locks** — not a setting.
- **Discovery survives the database being down.** Schema is cached and served with its age attached, so an agent
  can learn a table's shape and draft a statement offline. Staleness is always stated, never inferred.
- **Everything is audited, locally.** Append-only JSONL covering every request *including refusals*, with the
  caller, the statement, the outcome and the error code. It stays on the box; there is no control plane and
  [never will be](CLAUDE.md).

## Building

Requires the .NET SDK pinned in `global.json`, and Docker for the integration tests.

```bash
dotnet build
dotnet test                                   # 122 tests; adapters run against a real containerised PostgreSQL

# What actually ships. A JIT-only test run does not tell you how the published binary behaves.
dotnet publish src/Rtfq.Cli -c Release -r linux-x64
```

That last point is not ceremony. [ADR 0001](docs/decisions/0001-sql-parser-selection.md) found a statement guard
that scored 20/20 under the JIT and **3/20** as a published NativeAOT binary — classifying `DROP TABLE` as a
harmless read — because the trimmer had removed metadata a reflection-based tree walk depended on. Trim and AOT
warnings are build errors here, and CI runs the suite against the published artifact.

## Design

| Document | What it covers |
|---|---|
| [CLAUDE.md](CLAUDE.md) | What RTFQ is, its non-goals, and the principles that constrain every change |
| [docs/PHASES.md](docs/PHASES.md) | M0–M5, with exit criteria and the open decisions each phase depends on |
| [ADR 0001](docs/decisions/0001-sql-parser-selection.md) | Per-dialect parsers, and why the guard is an allow-list |
| [ADR 0002](docs/decisions/0002-ddl-additive-and-corrective.md) | Which schema changes an agent may make, and which it may never |
| [ADR 0003](docs/decisions/0003-no-cursor-pagination.md) | Why truncation is terminal, and how the cache handles staleness |
| [ADR 0004](docs/decisions/0004-m1-go-no-go.md) | The M1 go/no-go, with measured token costs |
| [CHANGELOG.md](CHANGELOG.md) | What changed in each release, and what each one still cannot do |

## Releasing

Tags drive releases; nothing reads a version out of a file, so a build can never claim a number its tag did not.

```bash
# 1. move the Unreleased section of CHANGELOG.md under a new [x.y.z] heading
# 2. tag and push
git tag -a v0.1.1 -m 'rtfq 0.1.1' && git push origin v0.1.1
```

The release workflow runs the full suite (integration tests included), publishes a NativeAOT binary per platform,
asserts each binary reports the tagged version, bundles it, and creates the GitHub release using that version's
changelog section as the notes. A tag with no matching changelog section fails the release rather than shipping
empty notes.

## Licence

[MIT](LICENSE).
