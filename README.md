# RTFQ — *Remote Tool For Queries*

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

Verify against `SHA256SUMS`, published with each release.

### Putting `rtfq` on your PATH

The binary depends on nothing but its own directory, so "installing" it is copying one file somewhere already on
your PATH.

**Linux** — `/usr/local/bin` is on every distribution's default PATH:

```bash
sudo install -m 0755 rtfq /usr/local/bin/rtfq
rtfq --version
```

**macOS** — the same, plus one step, because a binary downloaded with a browser is quarantined and Gatekeeper
will refuse to run it:

```bash
xattr -d com.apple.quarantine rtfq 2>/dev/null || true
sudo install -m 0755 rtfq /usr/local/bin/rtfq
rtfq --version
```

**Windows** — put it somewhere durable and append that directory to your user PATH. This needs no administrator
rights, and it survives reboots because it writes the environment variable rather than setting it for the session:

```powershell
$dir = "$env:LOCALAPPDATA\Programs\rtfq"
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item .\rtfq.exe $dir -Force

$path = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($path -notlike "*$dir*") {
    [Environment]::SetEnvironmentVariable('Path', "$path;$dir", 'User')
}
$env:Path += ";$dir"    # this session, without reopening the terminal

rtfq --version
```

If you would rather not install anything, every example below works with `./rtfq` in place of `rtfq`.

On Windows, [`scripts/windows/`](scripts/windows) does this and the rest of the setup — PATH, the environment
your config references, a certificate, and the firewall rule — with the traps written down.

Intel Macs and Windows on ARM are not built — see
[CHANGELOG.md](CHANGELOG.md) for why that is a decision rather than an oversight.

**Linux prerequisites.** The bundle needs `libicu` on the host — `apt install libicu72` or
`dnf install libicu`. Most server images already have it; minimal containers do not. This is not optional:
the SQL Server driver refuses to run without globalization support, so the binary aborts at the first query
rather than degrading. Windows and macOS need nothing extra.

Linux builds target **glibc 2.34**, so Debian 12, Ubuntu 22.04 and RHEL 9 are all supported. That floor is
asserted in CI, because it is set by whichever host compiles the binary and drifts upward silently.

## Try it

The sample config references its secrets rather than containing them, so the **server** needs both in its
environment. Neither name is special to RTFQ — `examples/rtfq.dev.yaml` picks them, and you can too:

```bash
export RTFQ_AGENT_SECRET='dev-token-please-change'                                # you invent this
export ORDERS_DSN='Host=localhost;Port=5432;Database=orders;Username=rtfq'        # your database

rtfq validate --config examples/rtfq.dev.yaml
rtfq serve    --config examples/rtfq.dev.yaml
```

`RTFQ_AGENT_SECRET` is the bearer token for the identity the config calls `agent`; whoever presents it gets that
token's grants and nothing else. `ORDERS_DSN` is how RTFQ itself reaches Postgres. **The agent never sees the
second one** — that separation is the whole point.

Then, from anywhere that can reach it. These two names *are* known to the CLI, and just save you passing
`--server` and `--token` on every command:

```bash
export RTFQ_SERVER='http://127.0.0.1:7420'
export RTFQ_TOKEN='dev-token-please-change'   # the same value as RTFQ_AGENT_SECRET above

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

## Reaching it from another machine

The sample listens on `127.0.0.1`, which is the right default and useless for the actual job: RTFQ is meant to
run on the box that can see the databases, with agents and people talking to it from elsewhere.

Moving the listener off loopback requires TLS. That is a rule rather than a config knob — `rtfq validate` refuses
`0.0.0.0` without a certificate, and there is no `--insecure` escape hatch on the server. So exposing the port is
three things: a certificate, a config change, and a hole in the firewall.

On Windows, [`scripts/windows/rtfq-cert.ps1`](scripts/windows/rtfq-cert.ps1) does steps 1 and 3 for you.

### 1. A certificate

A self-signed one is fine for a team on a private network. The **subject alternative name matters** — clients
validate against it, so it must list the name or address callers will actually use:

```bash
sudo mkdir -p /etc/rtfq
sudo openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout /etc/rtfq/tls.key -out /etc/rtfq/tls.crt \
  -subj "/CN=rtfq.internal" \
  -addext "subjectAltName=DNS:rtfq.internal,IP:10.0.0.5"
sudo chmod 600 /etc/rtfq/tls.key
```

<details>
<summary>PowerShell equivalent</summary>

```powershell
$dir = "$env:ProgramData\rtfq"
New-Item -ItemType Directory -Force $dir | Out-Null

$cert = New-SelfSignedCertificate -Subject "CN=rtfq.internal" `
    -DnsName "rtfq.internal", "10.0.0.5" `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(1)

# RTFQ reads PEM, so export rather than leaving it in the store.
$pem = [Convert]::ToBase64String($cert.RawData, 'InsertLineBreaks')
"-----BEGIN CERTIFICATE-----`n$pem`n-----END CERTIFICATE-----" |
    Set-Content "$dir\tls.crt" -Encoding ascii

$key = [Convert]::ToBase64String(
    $cert.PrivateKey.ExportPkcs8PrivateKey(), 'InsertLineBreaks')
"-----BEGIN PRIVATE KEY-----`n$key`n-----END PRIVATE KEY-----" |
    Set-Content "$dir\tls.key" -Encoding ascii
```
</details>

If you have a real certificate — an internal CA, or Let's Encrypt via DNS-01 for a private name — use that
instead and skip the `--insecure-skip-verify` caveat below.

### 2. Point the config at it

`cert` and `key` take **paths**, not `${file:...}` references. They are the one exception to *secrets are
referenced, never inlined*: RTFQ hands the paths to the TLS stack rather than reading them itself. Any location
works — there is no folder RTFQ looks in — but prefer absolute, since a relative path resolves against whatever
directory the server was launched from. Check too that the account running RTFQ can read the key file; a
permission problem there surfaces as a startup failure that reads like a bad certificate.

```yaml
server:
  listen: 0.0.0.0:7420
  tls:
    cert: /etc/rtfq/tls.crt
    key: /etc/rtfq/tls.key
```

Then check it before you open anything:

```bash
rtfq validate --config /etc/rtfq/rtfq.yaml --production
```

`--production` is worth using here even if this is not production. It turns the inline-secret warnings into
errors, which is exactly the review you want at the moment a port stops being private.

### 3. Open the port

```bash
# Debian / Ubuntu
sudo ufw allow 7420/tcp

# RHEL / Fedora
sudo firewall-cmd --permanent --add-port=7420/tcp && sudo firewall-cmd --reload
```

```powershell
# Windows, elevated
New-NetFirewallRule -DisplayName "RTFQ" -Direction Inbound `
    -Protocol TCP -LocalPort 7420 -Action Allow
```

Prefer scoping the rule to the callers you expect — `sudo ufw allow from 10.0.0.0/24 to any port 7420 proto tcp`,
or `-RemoteAddress 10.0.0.0/24` on Windows. A token is the only thing standing between the open port and your
databases, and a token is one leaked environment variable away from being public.

### Then, from a client machine

```bash
export RTFQ_SERVER='https://rtfq.internal:7420'
export RTFQ_TOKEN='the-secret-from-the-tokens-block'

rtfq sources
```

With a self-signed certificate the client will refuse it until the CA is trusted. `--insecure-skip-verify` gets
you moving, and it is a **client-side** flag: it cannot weaken anything the server enforces, but it does mean the
client stops checking who it is talking to. Fine while you are wiring things up; not something to leave in a
script that runs unattended.

### Do not put this on the public internet

The threat model is a private network with an authenticated port on it. There is no rate limiting, no lockout
after failed tokens, and static bearer tokens do not rotate. If callers are outside your network, put RTFQ behind
a VPN or an SSH tunnel — `ssh -L 7420:127.0.0.1:7420 user@host` needs no certificate, no firewall change, and no
`0.0.0.0`, and for one person on a laptop it is the better answer than everything above.

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

`rtfq approvals` is an ordinary client call, so it needs `RTFQ_TOKEN` and `RTFQ_SERVER` like any other — even
when you run it on the server itself, where the config's own `${env:...}` values are exported but the client
variables are not. Approving also requires a token granted write **somewhere**; a read-only one is refused. Give
the humans their own token rather than borrowing the agent's, so the audit log names a credential that is
actually theirs:

```yaml
      - id: alex
        secret: ${env:RTFQ_ALEX_SECRET}
        grants:
          orders: write
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

**The agent cannot approve its own write, and there is no tool that would let it.** That is the entire point: the
gate exists for the case where the agent has been persuaded by something it read, and an agent that can approve
itself is not gated at all. The approval has to arrive through a channel the agent does not control. The same
person can be both — you, at a second terminal — just not the same process.

**Nothing notifies you.** The default provider is a queue, not an inbox, so a proposal waits until it lapses
unless somebody looks. Two ways to not miss one:

```bash
rtfq approvals --watch          # stays open, prints each request as it arrives, asks [a/d/s]
```

Run that wherever you are — it is a client, not a server component, so your own machine is usually the right
place rather than the box RTFQ runs on. Several people can watch at once: whoever answers first wins, and the
request drops out of everyone else's queue on the next poll.

...or, for a team rather than one operator at a desk, hand the question to something that can reach people:

```yaml
approval:
  mode: webhook
  endpoint: https://approvals.internal/rtfq
```

That is how a Slack integration gets built without Slack living in the binary. Anything the endpoint says that is
not a recognised verdict — a timeout, a 500, malformed JSON, an unreachable host — counts as *not yet decided*,
never as approval.

### When to turn approval on

On any source where a small, in-bounds, semantically wrong write would matter — which is most of production. The
four structural gates stop blast radius; they do not stop `UPDATE customers SET tier = 'vip' WHERE id = 42`,
which is qualified, tiny, allow-listed, and possibly suggested by a poisoned row. Only a person reading the
statement catches that.

On a dev database it is usually the wrong tool, and asking a human to sign off on every scratch write is how
people end up removing the gate everywhere. Prefer a deliberate window instead:

```yaml
    require_approval: false
    require_unlock: true      # writes stay shut until: rtfq unlock <source> --write --ttl 15m
```

## Point an agent at it

```bash
rtfq mcp    # speaks MCP on stdio; RTFQ_SERVER and RTFQ_TOKEN select the server
```

Nine tools: `list_sources`, `describe_source`, `describe_table`, `sample`, `query`, `explain`, `propose_write`,
`commit_write`, `abort_write`. Meeting an unfamiliar four-table database and answering a question that needs a
join costs about **240 tokens end to end**; `describe_source` on a 202-table database costs about **379**. Those
numbers are asserted in CI and printed on every run, because an agent pays them on every call — see
[ADR 0004](docs/decisions/0004-m1-go-no-go.md).

### Claude Code

Drop a `.mcp.json` in the project root:

```json
{
  "mcpServers": {
    "rtfq": {
      "command": "rtfq",
      "args": ["mcp"]
    }
  }
}
```

No credentials in the file. `rtfq mcp` reads `RTFQ_SERVER` and `RTFQ_TOKEN` from the environment it inherits,
which is what you want: `.mcp.json` is project-scoped and usually committed, and a bearer token pasted into it is
a bearer token in your git history.

If the server uses a self-signed certificate, the client has to be told to skip verification — there is no
environment variable for it, because it is a decision worth making per invocation rather than once and forgetting:

```json
{
  "mcpServers": {
    "rtfq": {
      "command": "rtfq",
      "args": ["mcp", "--insecure-skip-verify"]
    }
  }
}
```

Better, once you are past wiring things up: import the server's `tls.crt` into the client machine's trust store
and drop the flag.

For a machine where the environment is awkward to set, Claude Code expands `${VAR}` in this file, so you can
point at variables by name rather than inlining values:

```json
{
  "mcpServers": {
    "rtfq": {
      "command": "rtfq",
      "args": ["mcp"],
      "env": {
        "RTFQ_SERVER": "${RTFQ_SERVER:-https://127.0.0.1:7420}",
        "RTFQ_TOKEN": "${RTFQ_TOKEN}"
      }
    }
  }
}
```

Check it took with `/mcp` inside Claude Code, or from a shell:

```bash
rtfq sources          # same credentials, same server, no MCP in the way
```

### Workflows, as MCP prompts

Alongside the tools, RTFQ exposes four **prompts** - procedures rather than capabilities. In Claude Code they
appear as slash commands:

| Prompt | For |
|---|---|
| `diagnose_slow_query` | A query timed out or read far more than expected. |
| `explore_source` | Getting oriented in an unfamiliar database without reading the whole schema. |
| `investigate_record` | Following one record across the tables that reference it. |
| `propose_fix` | Preparing a data change so a human can approve exactly what will happen. |

They live here rather than as a tenth tool for a reason worth knowing: a tool's description sits in the model's
context for the whole session, while a prompt is listed by name and its body only fetched when somebody runs it.
A long procedure costs nothing until it is wanted.

Each one carries out its work through the tools above, so everything a prompt leads to passes the same gates.
None asserts anything about your data - they say which tool to reach for and in what order, and a prompt is
fixed at build time while a schema is not.

Other clients take the same three pieces — command `rtfq`, args `["mcp"]`, and the two environment variables.
It speaks JSON-RPC 2.0 over stdio and writes its startup banner to stderr, so nothing pollutes the protocol.

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
