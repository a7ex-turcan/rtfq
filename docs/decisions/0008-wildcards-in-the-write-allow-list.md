# ADR 0008 — patterns in the write allow-list

**Status:** accepted · **Date:** 2026-08-18 · **Supersedes part of** [ADR 0006](0006-m3-write-path.md)

## Context

`writable_tables` accepted exact names only. The asymmetry was deliberate and written into the code: deny is a
glob because a deny rule matching too much is an inconvenience, allow is exact because an allow rule matching too
much is a table nobody meant to expose.

Then somebody pointed RTFQ at three development databases and wanted the whole `dbo` schema writable. The answer
the design gave was "enumerate every table", and for a schema of any size that is not an answer. It is the kind
of friction that ends with `access: write` on a source with no allow-list at all, or with the gate removed.

**A gate people route around protects nothing.** That is the argument that carried this, and it is worth stating
plainly rather than dressing it up as a feature request.

## Decision

`writable_tables` entries are globs, matched by the same routine `deny_tables` already used. `dbo.*` covers a
schema. `*` covers everything. An entry containing no `*` matches exactly as it always did, so every allow-list
written before 0.6.0 means precisely what it meant before.

Three properties do not move:

1. **Deny is evaluated first and wins.** This is what makes a wildcard defensible instead of reckless: `dbo.*`
   plus `deny_tables: ["*.payment_tokens"]` is a coherent, auditable position. Without a carve-out mechanism a
   wildcard would be a one-way door.
2. **An absent allow-list is still absent, not permissive.** Empty means nothing is writable. A wildcard is
   something you write down.
3. **Matching stays ordinal and case-sensitive.** `DBO.*` does not match `dbo.orders`. Folding case would be a
   gate bypass on PostgreSQL, where `Orders` and `orders` are different tables (ADR 0001).

The remaining asymmetry between the two lists is no longer *which supports a wildcard* — it is *which gets the
benefit of the doubt when both match*. That is the property worth keeping.

## The cost, stated rather than waved away

**A pattern covers tables that do not exist yet.** An exact list describes the schema as it is today. `dbo.*`
also describes whatever is created next month, so the day somebody adds `dbo.api_keys` to that database it is
writable and nobody decided that. This is precisely the failure exact-matching prevented, and enabling a wildcard
is accepting it.

Two things mitigate it and neither eliminates it:

- **The validator warns**, per pattern, naming it and pointing at `require_approval` and `deny_tables`. A gate
  that got wider should not do so quietly. It is a warning rather than an error because this is a legitimate
  choice somebody made on purpose — refusing it would just push them to `deny_tables`-only, which fails open.
- **The scoped database grant is still the real boundary** (principle 5). A login that cannot write to
  `dbo.api_keys` is unaffected by what our allow-list says about it. This is the mitigation that actually holds,
  and it is the one to reach for.

## Alternatives rejected

**A separate `writable_schemas: [dbo]` key.** Narrower and arguably clearer — it cannot express `*` across an
entire database. Rejected because it is a second key and a second pattern language for one concept, and CLAUDE.md
prefers deleting a feature to adding a config knob. One list, one matcher.

**Allow `dbo.*` but refuse a bare `*`.** Considered, and it is theatre: on a database with a single schema they
are the same set. A rule that stops nothing while implying it stops something is worse than no rule.

**Expand the pattern at startup against the live schema.** Turn `dbo.*` into the concrete table list when the
server boots, so the allow-list is always explicit and a table added later is *not* included until a restart.
Genuinely attractive — it removes the whole cost above. Rejected for now because it makes the gate depend on
source reachability, and offline operation is load-bearing here: a source that is down at boot would produce an
empty allow-list, which fails closed but confusingly. Worth revisiting if the silent-widening problem bites
somebody in practice.

**Do nothing and tell people to enumerate.** The status quo, and what was recommended first. Rejected once the
answer to "make my dev schema writable" turned out to be a hundred lines of YAML per source.

## Consequences

- `rtfq validate` emits `source.writable_wildcard` for each pattern. Expect it in the output of any config that
  uses one; it is not a problem to fix.
- The write path's blast radius on a wildcard source is now bounded by `max_affected_rows`, the statement guard,
  `deny_tables` and the database grant — but no longer by an enumerated table list. On anything that matters,
  `require_approval` moves from advisable to load-bearing.
- Twelve unit tests cover the matcher, including the cases where a pattern could plausibly reach further than it
  reads (`dbo_secret.keys`, `xdbo.orders`, a three-part name). Three integration tests cover it against a real
  PostgreSQL, one of which asserts that a pattern on one source does not widen a differently-configured source
  sharing the same database.
