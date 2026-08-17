# ADR 0005 — M2 interface audit: the adapter interface held

- **Status:** Accepted
- **Date:** 2026-08-15
- **Decides:** whether `ISourceAdapter` survived three more engines, and what it cost

---

## The question

M2's deliverable is not three adapters. It is the evidence that one interface
describes PostgreSQL, SQL Server, MongoDB and an HTTP API — because
[`CLAUDE.md`](../../CLAUDE.md) says that if the core has to change to accommodate
one engine's quirk, the interface is wrong and we should say so rather than work
around it.

The exit criterion was **zero dialect-specific branches above `Rtfq.Adapters`**.

## Verdict: it held, with one interface change and one leak found and fixed

**213 tests pass**, including a shared conformance suite that every adapter runs
unmodified against a real containerised instance. That suite is the argument: it
contains no per-adapter branching, so any behaviour needing a special case would
have had nowhere to live.

## What changed, and why each was not a leak

### 1. `CheckAsync` now returns capabilities — an interface change, correctly

It was `Task`; it is now `Task<SourceCapabilities>`.

MongoDB supports transactions on a replica set and not on a standalone. Nothing
in the config reveals which is deployed, so `Capabilities` could not be a
constructor-time constant. That is not Mongo being awkward — it is the interface
having assumed something untrue of the general case, and PostgreSQL and SQL
Server simply never exposed the assumption.

The fix generalises rather than special-cases: every adapter returns what it can
observe, and the two SQL adapters return their static answer.

### 2. Validation split into static and connect-time — a consequence, not a leak

The same fact forced a second change: `rtfq validate` must work offline, so it
cannot decide whether a Mongo source may declare `access: write`.

Validation is therefore in two parts. Static rules stay in `ConfigValidator`;
capability rules that need a live server run at startup via
`SourceRegistry.CheckCapabilitiesAsync`. **An unreachable source is a warning, not
a failure** — refusing to boot because one database is down would contradict the
offline-discovery posture the schema cache exists to provide.

### 3. Document/row impedance — resolved inside the adapter

MongoDB returns documents, not rows, and the envelope is columnar. The
temptation was a second response shape; instead the adapter flattens — columns
are the union of top-level fields in first-seen order, absent fields are null,
nested structure renders as JSON. The HTTP adapter does the same for a JSON array.

Nothing above the adapter learned that documents exist.

### 4. "Statement" stayed a string, and it was not a fudge

`ExecuteReadAsync(string statement, …)` looked SQL-shaped and was the most likely
thing to break. It did not, because every one of these sources genuinely has a
native textual dialect:

| Source | A statement |
|---|---|
| PostgreSQL / SQL Server | `SELECT id FROM orders WHERE status = 'stuck'` |
| MongoDB | `{"find": "orders", "filter": {"status": "stuck"}}` |
| HTTP | `GET /v1/invoices?status=open` |

Mongo's command document *is* its wire format, not a shape we invented. That
matches `CLAUDE.md`'s rule that each source is queried in its native dialect.

## The leak the audit found

`ConfigValidator` had grown two hardcoded lists — `TransactionalDdlKinds` and
`NonTransactionalKinds` — naming which engines support what. That is capability
knowledge living **above** the adapter layer: adding an engine would have meant
editing the validator, which is precisely the coupling this phase exists to
detect.

Fixed by moving it into `AdapterCatalog` in the adapter layer, which owns what
kinds exist and what each can do without connecting. The validator now asks.

Remaining and accepted: `SourceRegistry` switches on `kind` to construct
adapters. That is a factory, and a factory mapping a name to a constructor is the
one place that must know the mapping.

## What each engine needed that the others did not

Recorded because these are the shapes M3 will have to guard, and each is that
dialect's version of ADR 0001's finding that a statement-type allow-list is not
enough.

- **SQL Server** bounds results with `TOP` inside the SELECT rather than a
  trailing `LIMIT`, so the limit is set on the query expression and the script
  regenerated. It also permits several statements in one batch **with no
  separator at all** — `SELECT 1 DROP TABLE widgets` is two statements — which
  makes the multi-statement check matter more than in PostgreSQL.
- **MongoDB** has `$out` and `$merge`: aggregation *stages* that write a
  collection, and `$where`/`$function`/`$accumulator`, which execute server-side
  JavaScript. None is a write *command*, so the obvious design — a command-name
  allow-list — waves every one through. The guard walks the whole document, which
  is what catches `$out` buried inside a `$facet`.
- **HTTP** has no query engine to guard, so the gate is entirely the allow-list.
  A wildcard path plus a write method is a config **error**, per `CLAUDE.md`: each
  half is harmless and together they hand over the whole API.

## What M2 cost

Two of these are unwelcome and neither was foreseen.

- ⚠ **`InvariantGlobalization` had to be turned off, and it costs the
  self-contained property on Linux.** `Microsoft.Data.SqlClient` throws
  `NotSupportedException: Globalization Invariant Mode is not supported` on *every*
  connection attempt, so the setting had to go.

  The consequence is that **a Linux host now needs `libicu` installed**. The
  binary aborts at the first operation that touches `CultureInfo` with
  *"Couldn't find a valid ICU package"*. Windows and macOS are unaffected — they
  use OS globalization.

  This was nearly missed: an early check ran `rtfq --version` on an image without
  libicu, saw it succeed, and concluded there was no dependency. `--version` never
  touches globalization. The same false-negative shape as the unreferenced AOT
  probe earlier in this phase — **a passing test of the wrong code path.**

  `StaticICULinking=true` would restore self-containment and does not currently
  build; that is the open lead.
- **The binary grew from 20 MB to 66 MB** — 54 MB from the three new packages,
  plus 12 MB for rooting the BSON assemblies.
- ⚠ **MongoDB does not work under AOT without rooting its assemblies, and the
  reason invalidates the assumption the suppression was granted on.** The premise
  was that `BsonDocument`-only usage avoids the reflective POCO machinery. It does
  not: `BsonDocument.Parse` — the first line of the Mongo guard — reaches
  `BsonDefaults.DynamicArraySerializer` and dies with `MissingMethodException`
  because the trimmer removed a serializer constructor it looks up reflectively.

  Found only by running the published binary against a real MongoDB. `dotnet test`
  passes either way, because tests run under the JIT.

  Fixed with `TrimmerRootAssembly` for `MongoDB.Bson` and `MongoDB.Driver`, and
  re-verified end to end: reads work, `$out` and `$where` are refused, and inferred
  schema reports `total double|int32` correctly.
- ✔ **A new glibc floor of 2.38 — found and fixed.** `fmod`/`fmodf` came in with
  the new dependencies, and a binary links against the glibc that built it, so
  building on the 24.04 runner produced an artifact that **would not start on
  Debian 12, Ubuntu 22.04 or RHEL 9**.

  Fixed by building the Linux artifacts inside an Ubuntu 22.04 container, which
  brings the floor to **2.34** — low enough for RHEL 9, the oldest of the three.
  Verified by running the result on Debian 12 and Ubuntu 22.04. CI asserts the
  ceiling and uses the same build path as the release, so the artifact tested is
  the artifact shipped.

  A musl build would have removed the question entirely, but `Npgquery` ships
  glibc-linked native libraries, so it is not available.
- **Six dependencies are not AOT-clean**, not one as first assumed:
  `Microsoft.Data.SqlClient` and its logging assembly, `ScriptDom`, `MongoDB.Bson`,
  `MongoDB.Driver` and `System.Configuration.ConfigurationManager`. ScriptDom is
  the one that matters most, because it sits on the **guard** path — the exact
  place ADR 0001 found a trimmed reflection walk failing open.

  So it was tested rather than argued: the published AOT binary refuses an
  `UPDATE`, refuses `EXEC`, refuses `$out` and refuses `$where`, all through the
  trimmed code. The rollups stay visible in the publish log and CI asserts the set
  of dirty assemblies is exactly this one.

  Residual risk: a code path neither the conformance suite nor this verification
  exercises could still fail only in the AOT build. That risk is now bounded by
  what the end-to-end check covers, which is why it covers the guards.

## Consequences

- M3 is unblocked, and now has three more guards to write rather than one.
- `AdapterCatalog` is where a fifth engine gets declared. If a future adapter
  needs anything above `Rtfq.Adapters` to change, that is a finding, not a task.
- The glibc floor is an open release blocker.
