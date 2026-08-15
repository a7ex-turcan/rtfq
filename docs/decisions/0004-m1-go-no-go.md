# ADR 0004 — M1 go/no-go: GO

- **Status:** Accepted
- **Date:** 2026-08-15
- **Decides:** whether the discovery and read surface is good enough to build M2–M5 on

---

## The question

`docs/PHASES.md` makes M1 the go/no-go milestone, on the grounds that *"if the discovery and read tools are
unpleasant for an agent, nothing downstream saves us"*. The test is not whether the tools work. It is whether an
agent can meet an unfamiliar database and answer a real question without spending its context to do it.

## Verdict: **GO**

## Evidence

A full agent workflow against a four-table storefront database it had never seen — discover the source, learn two
table shapes, then answer a question requiring a join and a group-by:

| Call | Cost |
|---|---|
| `list_sources` | ~16 tokens |
| `describe_source` (4 tables) | ~46 tokens |
| `describe_table public.orders` | ~90 tokens |
| `describe_table public.customers` | ~61 tokens |
| `query` (join + group by, 4 rows) | ~29 tokens |
| **Whole question, discovery included** | **~242 tokens** |

And at the size where compactness actually matters, measured in CI on a **202-table** database:

| Output | Measured | Ceiling |
|---|---|---|
| `list_sources` | ~10 tokens | 100 |
| `describe_source` | **~379 tokens** | 1500 |
| `describe_table` | ~83 tokens | 400 |

The one-off cost of `tools/list` is ~696 tokens, paid once per session for six tools.

`describe_source` staying under 400 tokens on a 202-table database is the number that decides this. It is not
achieved by hiding tables — the response reports the true count and clips the *list* at 80 with an explicit hint
to filter — but by refusing to spend tokens on structure the agent can infer: tables grouped under a schema
header instead of repeating the qualified prefix on every line, row counts rounded to `~3k`, column counts as
`5c`.

These numbers are asserted in CI and printed on every run, so the failure mode is a visible trend rather than a
sudden breach.

## What dogfooding changed

Two things only became obvious with real output in front of us, and both were pure waste:

- **Sequence defaults.** A serial primary key rendered as
  `default=nextval('orders_id_seq'::regclass)` — about a dozen tokens, on most tables, saying nothing an agent
  can act on. Now `auto`.
- **SQL-standard type spellings.** `timestamp with time zone` became `timestamptz`, the alias the same engine
  accepts and its own documentation uses. Nothing is lost; half the characters are.

Together those took 13–18% off `describe_table`, which is the most-called discovery tool. Neither would have
been found by reading the code.

## What is still weak

Recorded now so it is not rediscovered as a surprise.

- **The dogfood database was synthetic**, and its seed correlated country with order status, so the question's
  *answer* was an artifact of `generate_series` arithmetic. What was proven is the workflow and its cost, not
  that RTFQ produces insight. A real database with real skew is the honest next test.
- **`describe_source` clips at 80 tables.** On a database of thousands, an agent that does not know what it is
  looking for gets a hint to filter rather than an answer. Acceptable — the alternative is a dump nobody can
  afford — but it means discovery on very large estates depends on the agent guessing a useful pattern.
- **No column statistics.** An agent choosing a `WHERE` clause would benefit from knowing that `status` has four
  distinct values. `pg_stats` has this. It was left out of M1 because it is the sort of field that quietly
  doubles `describe_table`; revisit with a measurement, not an intuition.
- **`sample` was barely exercised** in the dogfood pass. The workflow that emerged went `describe_table` →
  `query`, skipping it. If that holds with real use, `sample` is a tool earning its context cost on every call
  without being used, and CLAUDE.md is explicit that this is grounds for removing it.

## Consequences

- M2 (SQL Server, MongoDB, HTTP adapters) is unblocked.
- The `describe_*` rendering is now a **measured contract**, not a preference. Anything added to it has to be
  paid for out of the same ceiling.
- Watch `sample` through M2. If it stays unused, delete it — preferring to delete a feature is the standing rule.
