# RTFQ — *Run The F\*\*\*ing Query*

A single self-hosted binary that gives AI agents and developers **governed, schema-aware, auditable access to
heterogeneous data sources** on a machine they cannot otherwise reach.

One binary, one YAML file, five minutes to first query. **Read by default, write on a leash.**

> **Status: M0 — walking skeleton.** PostgreSQL reads over an authenticated TLS port, capped and audited.
> The MCP surface lands in M1 and the write path in M3. See [docs/PHASES.md](docs/PHASES.md).

---

## Why

Letting an agent answer *"why is this order stuck?"* — let alone fix it — currently means handing it a
production connection string, standing up one MCP server per database, or buying an enterprise access platform.
RTFQ is the option for the eight-person team: one binary that holds the config, the credentials, the policy, and
the audit log, and exposes a port that agents and humans both talk to.

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
rtfq query --source orders "SELECT id, customer, total FROM orders WHERE vip" --max-rows 20
```

```
id   customer      total
---  ------------  ------
7    customer-7    10.50
14   customer-14   21.00

2 rows, 4 ms
```

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
- **Everything is audited, locally.** Append-only JSONL covering every request *including refusals*, with the
  caller, the statement, the outcome and the error code. It stays on the box; there is no control plane and
  [never will be](CLAUDE.md).

## Building

Requires the .NET SDK pinned in `global.json`, and Docker for the integration tests.

```bash
dotnet build
dotnet test                                   # 72 tests; adapters run against a real containerised PostgreSQL

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

## Licence

[MIT](LICENSE).
