# ADR 0003 — Truncation is terminal; the schema cache serves stale

- **Status:** Accepted
- **Date:** 2026-08-15
- **Decides:** two open questions from [`docs/PHASES.md`](../PHASES.md) that M1 could not start without
- **Amends:** the M1 exit criterion that required "a usable `next_cursor`"

---

## Part 1 — No cursor pagination

### Context

An over-cap read has to end somehow. `docs/PHASES.md` listed three options and leaned toward re-executing with
`OFFSET`. That lean was wrong, and this ADR reverses it.

Arbitrary caller-authored SQL cannot be keyset-paginated generically: there is no guaranteed unique ordering to
resume from. That leaves offset re-execution, a held server-side cursor, or no pagination at all.

### Decision

**`next_cursor` stays in the envelope and is always `null`.** A response that hits the cap is `truncated: true`
and terminal. The caller narrows the predicate, aggregates, or lowers its own ambitions.

The field stays rather than being removed because the envelope shipped in v0.1.0 and removing it would be a
breaking change for no gain — and because if a future dialect offers a safe cursor, the contract is already
shaped for it.

### Why not the alternatives

**Offset re-execution** is worse than it looks. Page N re-runs the entire query and discards N rows, so a caller
walking twenty pages runs twenty full scans of a production table. The cost is `O(N)` per page against exactly
the database we promised to be careful with, and the result is not even snapshot-consistent: rows shift between
pages as the table changes underneath. It is the option that reads simplest in a design document and behaves
worst in production.

**A held server-side cursor** is snapshot-consistent and avoids the re-scan, but each open cursor pins a
connection idle-in-transaction. On PostgreSQL an idle transaction blocks `VACUUM` from reclaiming dead tuples
across the whole database, which is a slow-motion outage rather than a slow query. M3 already has to introduce
one held-resource lifecycle for write handles; adding a second one in M1, for *reads*, doubles the surface where
a leaked handle degrades a production server.

**And the deciding argument is the product's own grain.** `CLAUDE.md` principle 4 says the MCP surface exists to
be designed for a token budget. An agent paging three thousand rows into its context has already lost the thread
— it has spent its budget and learned less than one `GROUP BY` would have told it. Pagination would be
machinery whose main effect is to make the wrong behaviour convenient.

### Consequences

- A truncated response must tell the caller **what to do about it**, not merely that it happened. The envelope
  already carries `truncated` and `row_count`; the tool description and the CLI both say: narrow, aggregate, or
  pass a smaller `max_rows`.
- Callers that genuinely need bulk extraction are out of scope. That is consistent with non-goal #3: RTFQ is not
  a BI tool, and it is not an export pipeline either.
- Revisit only if the dogfood pass produces a concrete question an agent could not answer without paging. A
  hypothetical is not enough to buy the `VACUUM` hazard.

---

## Part 2 — The schema cache serves stale, then refreshes

### Context

`docs/PHASES.md` left open whether `describe_*` should re-introspect proactively on a cache miss, or serve stale
data and refresh in the background. The stated tiebreak was "whichever keeps `describe_*` fast".

### Decision

- **Cache hit, fresh** — serve it.
- **Cache hit, stale** — serve it *immediately*, flagged with its age, and refresh in the background.
- **Cache miss (nothing on disk)** — introspect synchronously. There is nothing else to serve, and blocking is
  better than answering "I don't know" about a database that is right there.
- **Source unreachable** — serve whatever is cached, flagged with its age. Only a cold miss against an
  unreachable source is an error, and it returns `source.unreachable`.

Staleness is **always** present in the response, never inferred from silence. Offline discovery is a feature;
hidden staleness is a bug.

### Why

The blocking case is the one that hurts. An agent drafting a statement calls `describe_table` several times in a
row; making each of those wait on a live catalog query against a loaded production database turns discovery into
the slowest part of the loop. Serving the cached answer and refreshing behind it costs the agent nothing and
costs the database one introspection instead of several.

The synchronous cold miss is deliberate asymmetry: "stale" only means something when there is something to be
stale *about*.

### Consequences

- A background refresh failure must not surface as a request failure. It is logged and the age keeps climbing —
  the agent can already see that, because the age is in every response.
- `rtfq refresh <source>` forces a synchronous re-introspection, so an operator who has just run a migration has
  a way to make the cache correct without waiting for a TTL.
- The cache is written atomically (temp file, then move), because a half-written schema file that survives a
  crash would be worse than no cache at all.
