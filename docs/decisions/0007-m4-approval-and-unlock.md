# ADR 0007 — M4: the human gate, and what it costs to hold one open

**Status:** accepted · **Date:** 2026-08-18 · **Milestone:** M4

## Context

M3 shipped the write path: propose runs the statement inside a transaction, captures before-images, reports the
real affected-row count, and holds the transaction open until commit or abort. That is the correct shape when a
handle lives for milliseconds.

M4 adds the gate `CLAUDE.md` calls the central control against a well-formed malicious write: a human reads the
statement and the rows before anything is saved. A human is not a machine. The handle now lives for as long as
somebody takes to answer.

## Decision 1 — an approval-required proposal holds nothing

Two handle shapes, and the difference is the whole milestone.

Without approval, a handle holds an open transaction: fastest, strictly serialisable, settled in milliseconds.
**With approval it holds nothing.** The statement runs, the diff is captured, and it rolls straight back.

The alternative — keep the transaction open while the approver thinks — was rejected on evidence from M3. On SQL
Server an open proposal blocks readers of the same rows; the M3 suite deadlocked on exactly this until
`READ_COMMITTED_SNAPSHOT` was enabled, and that only fixes readers, not writers. On PostgreSQL an idle-in-
transaction session holds back `VACUUM` for its whole lifetime. A gate whose cost is *an incident's worth of
blocking, every time somebody steps away from their desk* would be turned off, and a gate that gets turned off
protects nothing. The ergonomics of this feature are a security property.

### What that costs, and how it is paid

Not holding means the world can move underneath an approval. So commit re-runs the statement and compares a
**fingerprint** — SHA-256 over the statement, the affected count and the before-image JSON, truncated to 16 hex
characters. Identical fingerprint, commit proceeds. Anything else and it rolls back:

> refused and rolled back: the data changed after this was approved, so the approval no longer describes it.

Fingerprinting the statement alone would have been useless: the same SQL over different rows is a different
change, and that is precisely the case the gate exists for. A deliberately weakened fingerprint (statement only)
makes `An_approval_stops_applying_once_the_data_moves_underneath_it` fail, which is how we know the test is
testing something.

This is optimistic concurrency, and it is the honest trade. A pessimistic gate is a lock held by a human.

## Decision 2 — two providers, because one is not a seam

`IApprovalProvider` has three methods: request, poll for a decision, withdraw. Two implementations ship.

`LocalApprovalProvider` is the reference: an in-memory queue this server exposes over its own API and `rtfq
approvals` reads. In memory on purpose — a pending approval is only meaningful while its handle is alive, and
handles do not survive a restart, so persisting approvals would have an operator approving something that no
longer exists.

`WebhookApprovalProvider` is the second, and its existence is the point. One implementation is not a seam; it is
an abstraction nobody has tested. It also answers the question M4 was carrying: **what "plugin" means under
NativeAOT.** AOT rules out `AssemblyLoadContext`, so a plugin cannot be a DLL dropped into a folder. A webhook is
a boundary that survives AOT, keeps our binary free of anyone else's SDK, and lets an integration be written in
whatever language its author prefers. This is how Slack gets built without Slack living in core.

Selecting one is a config decision (`approval.mode`), and the broker cannot tell the difference. Under a webhook
provider `/v1/approvals` refuses with "the queue is not here" rather than serving an empty list — an empty list
reads as *nothing is waiting*, which is the one answer that must never be wrong.

Everything the webhook provider can be told that is not a recognised verdict — a 500, a timeout, `{"state":"yes"}`,
an HTML error page, a dead socket — yields **pending**, never approved. Unreachable is deliberately not a denial
either: the handle expires on its own, and a flapping endpoint should not destroy a proposal a human was about to
accept.

## Decision 3 — the approver sees the statement and the rows, and nothing else

`ApprovalContext` carries source, token, target, statement, affected count, before-image columns and rows, and the
fingerprint. It has **no field an agent can write prose into**, and `rtfq approvals` renders exactly those fields.

This is structural rather than a rule to remember. Per `CLAUDE.md` principle 3, tool output must never influence
policy, and the case this gate exists for is an agent persuaded by a poisoned row — such an agent writes a very
reassuring summary. There is nowhere for one to go.

## Decision 4 — the unlock is not persisted, and that is not configurable

`require_unlock: true` means writing stays off at runtime even where the config permits it, until somebody runs
`rtfq unlock SOURCE --write --ttl 15m`. A config granting write is a statement about what is *possible*, not about
what should be possible right now: copying a staging config to production, or leaving a grant in place after the
incident it was added for, should not leave a loaded gun lying around.

- Expiry is evaluated **on read**, not swept by a timer, so there is no window in which a lapsed unlock is still honoured.
- TTL is clamped to **one hour**. A window measured in hours is not a window.
- **A restart re-locks.** Nothing is written to disk, and there is no setting to change that: an unlock that
  outlives the thing it was scoped to is not time-boxed.
- Reads are never locked. Locking them would break the discovery this server exists for.
- An unlock for `write` does not open `schema`.
- Opening a source requires the access being opened. Handing out the right to open a door you cannot walk through
  would be a strange privilege.

The unlock gate sits **ahead of** the allow-list checks in the broker, so a locked source reports that it is locked
rather than enumerating which tables it would otherwise have permitted.

## What the dogfood found

Three defects that the JIT test suite could not have caught, because they were not about behaviour:

1. **The MCP hint was stale.** An approval-required proposal told the agent *"commit will be refused until an
   approver exists (M4)"* — M3's text, still shipping in M4. An agent reading that aborts instead of waiting,
   which defeats the feature entirely. It now explains that nothing is held, that `commit_write` answers `pending`
   until somebody decides, and when the request lapses.
2. **`describe_table` hard-coded `Writable = false`.** Left over from before M3. Discovery was telling agents they
   could not write to tables they could write to — in a tool whose whole premise is that agents plan from
   discovery. It is now the intersection of the real gates: source access, token grant, and the write allow-list.
   Deliberately *not* the unlock, which is a fact about right now rather than about the table; reporting a merely
   locked source as unwritable would hide the affordance that tells someone to unlock it.
3. **The HTTP adapter promised writes "arrive in M3".** They did not, and they are not coming. HTTP sources are
   read-only *by design*: the write path is built on a transaction that can capture before-images and roll back,
   and a request that has been sent cannot be un-sent. The refusal now says so.

A fourth, in the tests rather than the product: adding a fifth container fixture pushed the adapter suite past
what Docker on this machine would carry, and Testcontainers' reaper began killing containers out from under
running tests — surfacing as nine unrelated failures with no reproducible cause. Collection parallelism is now
capped at three in `xunit.runner.json`.

## Consequences

- An approval-required write is **not** serialisable end to end. Two agents proposing conflicting changes both get
  approved, and the second commit is refused as stale rather than merged. Correct, and worth stating plainly.
- Approvals do not survive a restart, so an in-flight change is lost rather than silently resumed.
- Approver identity is only as strong as the token presented. Any token with write access somewhere may see and
  answer the queue. That is weaker than a separate approver identity, it is stated plainly in the endpoint's own
  documentation rather than dressed up, and it waits for the identity work deferred past M5.

## Alternatives rejected

**Hold the transaction and let the approver block writers.** Rejected on the M3 evidence above.

**Extend the handle TTL on pending approval.** Considered, then made unnecessary: an approval-required handle
holds no resources, so it simply gets a longer TTL from the start (`approval_ttl`, default 10m) rather than a
sliding one. Nothing to keep alive means nothing to renew.

**Persist unlocks across restart.** Rejected. See decision 4.

**One provider now, a second when somebody needs it.** Rejected. An untested seam is not a seam, and the shape of
the interface is exactly what a second implementation checks.
