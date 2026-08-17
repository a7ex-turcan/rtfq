# ADR 0006 — M3: the write path

- **Status:** Accepted
- **Date:** 2026-08-17
- **Decides:** how a mutation gets from an agent to a committed row, and what stops it

---

## The question

`docs/PHASES.md` says M3 ships on its adversarial suite rather than on the
feature working. Writes are what make RTFQ more than another read-only MCP
server, and they are also the thing that can destroy a customer's Tuesday, so
the deliverable is the evidence.

## Verdict: shipped for PostgreSQL and SQL Server

**282 tests pass**, including 50 that exercise the write path end to end against
real containerised databases. Where a test asserts a refusal it also re-reads the
data from a separate connection, because a gate that reports "no" while letting
the write through is the only failure that matters.

## The mechanism

`propose_write` runs the statement inside a transaction and **stops**. The
driver reports how many rows it really touched — from the execution, never an
estimate — and only then does anything decide whether that was acceptable. Over
the cap rolls back and refuses **with the real count**. `commit_write` settles it.

The agent is structurally unable to reach a committed change without first
receiving a diff and acting on it. That is the propose/commit split doing its
actual job, which is not ceremony: per CLAUDE.md's central tension, the four
structural gates stop blast radius and cannot stop intent, and this is the step
where intent becomes visible.

### The four gates

Evaluated in order, each with its own error code and its own tests:

1. the **source** declares `write` (or `schema`)
2. the **token** was granted it — effective access is the intersection, so
   enabling a write always takes two edits in two places
3. the **target** is on `writable_tables`
4. the **statement** passes the guard

Gate three is asymmetric on purpose. **Deny is a glob** (`*.pii_*`) because a
deny rule matching too much is an inconvenience; **allow is exact** because an
allow rule matching too much is a table nobody meant to expose. Deny also applies
to everything a statement *touches*, not just its target — `UPDATE orders SET
note = (SELECT token FROM payment_tokens …)` is refused, because a write to an
allowed table that reads a denied one is still reading it.

## Decisions worth recording

### Repeatable read, not read committed

Before-images are captured inside the same transaction as the mutation. Under
read committed each statement takes a fresh snapshot, so the capturing SELECT and
the UPDATE could see different rows and **the journal would describe rows that
were never changed** — worse than no journal, because it would be believed. A
serialization failure under repeatable read is the correct outcome: better a
refused write than a false record of one.

### The write must *be* the statement

A `DELETE` inside a CTE under a top-level `SELECT` is refused. It has no single
unambiguous target, so the allow-list and the row cap would both be guessing, and
a gate that guesses is not a gate. Detecting the construct was already required
by ADR 0001; M3 decides what to do about it.

### Four open handles per source, as a constant

Every open proposal is a held connection and a held lock set, and on PostgreSQL an
idle transaction also holds back `VACUUM`. This is deliberately **not** a config
knob: an operator raising it to escape a symptom would be making the problem
worse. Four concurrent proposals means something upstream is wrong.

### `require_approval` refuses rather than queues

There is no approver until M4. A source marked `require_approval` rolls the
proposal back and says so, rather than parking it somewhere nobody will look.

## What the suite found

Four real defects, none of which would have survived to production but all of
which read as correct:

- The before-image query cloned a `RangeVar` between the two shapes libpg_query
  stores it in — wrapped inside a `Node*` list, unwrapped where the field is typed
  `RangeVar*`. It failed to render at all, with "Unknown field: relname".
- `UPDATE … FROM` produced a before-image query referencing an alias it never
  declared, because the extra relations live on the mutation's own `fromClause`.
- **Schema statements carried an empty statement string**, so DDL could never have
  executed. Invisible in review; obvious the first time a test ran one.
- `TOP (10000)` — the idiomatic parenthesised form — parsed as a
  `ParenthesisExpression`, so the tightening check declined to touch it and an
  over-cap TOP stayed in place.

## A dialect difference operators need to know

**On SQL Server, an open proposal blocks readers of the affected rows.** The
transaction holds exclusive locks, and a concurrent `SELECT` waits rather than
seeing the pre-image. PostgreSQL's MVCC shows readers the old rows instead.

Both are correct — nobody sees uncommitted data — but the consequence differs
sharply: on SQL Server an abandoned handle **stalls other queries**, not merely a
connection. That makes the handle TTL an availability control there, not just
hygiene. `READ_COMMITTED_SNAPSHOT` is the usual answer and the suite tests against
it. This belongs in the M5 posture document.

## What M3 does not include

- **MongoDB writes.** They need a replica set, and the propose/commit split
  depends on holding a session transaction across two requests, where Mongo's own
  transaction timeout interacts with our handle TTL. That needs its own
  adversarial suite; shipping the mechanism without one would be shipping the
  appearance of a gate. Refused with a typed code.
- **HTTP writes.** No transactions exist to leave open, so a change could not be
  shown before being kept. Refused at config validation and again at the adapter.
- **Human approval.** M4.

## Open

- **The unbounded pre-check** (`docs/PHASES.md`) is still not implemented. An
  uncommitted runaway still does its work before being rolled back;
  `statement_timeout` and `lock_timeout` are the current mitigations, and neither
  is complete.
- **Before-image size ceiling.** Values truncate at 512 characters with a marker,
  but there is no ceiling on the number of rows beyond the cap itself.
