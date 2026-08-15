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

The one-off cost of `tools/list` is ~696 tokens, paid once per session for six tools. `explain` on the same
join costs ~149 tokens.

*(These figures come from a clean re-run against a freshly published binary. An earlier pass measured the same
per-call numbers but did so after a partially-failed rebuild — the server process held file locks — so it was
repeated from an isolated build rather than left resting on an argument about which assemblies were stale.)*

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

A third observation cost nothing to fix because the design was already right, and is the clearest evidence for
[ADR 0003](0003-no-cursor-pagination.md):

| Call | Cost | Outcome |
|---|---|---|
| `SELECT * FROM order_lines` | **~1395 tokens** | 200 of 9000 rows, `TRUNCATED`, question unanswered |
| the aggregate its hint suggests | **~12 tokens** | complete and correct |

A hundred-fold difference, in favour of the cheaper call. This is the whole argument against pagination in one
measurement: had the truncated response offered a cursor, the obvious next move would have been to fetch page
two — forty more calls, forty full scans, and an answer worse than one `count(*)`. The hint is not a courtesy
message; it is the mechanism.

## What is still weak

Recorded now so it is not rediscovered as a surprise.

- **The row cap is denominated in the wrong unit.** `max_rows` bounds rows; what an agent actually spends is
  tokens, and the two are only loosely related. Two hundred rows of five narrow columns cost ~1395 tokens; two
  hundred rows of forty wide columns would cost an order of magnitude more, through the same cap. The cap
  therefore does not bound the thing the MCP surface exists to protect. A byte or token ceiling alongside the row
  count is the obvious answer, and it is not obvious what the default should be — worth deciding before M5,
  because changing a cap is an API change.
- **The dogfood database is synthetic.** Its first seed accidentally correlated country with order status, so an
  early run reported every stuck order in one country — arithmetic, not insight. Reseeding with a modulus coprime
  to the country count produced a real distribution (FR 196, GB 195, US 180, DE 179), which is what the numbers
  above reflect. Synthetic data with no skew, no nulls and no dirty rows is still a soft test; a real database is
  the honest next one.
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
