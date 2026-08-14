# Parser spike — throwaway

Evidence for [ADR 0001](../../docs/decisions/0001-sql-parser-selection.md). **Not part of the RTFQ build**: its
own Go module, no relationship to `cmd/rtfq`, and it will be deleted once M3 lands.

It is kept in the repo because the ADR's claims should be re-runnable when a dependency is bumped, and because
`corpus/corpus.go` is the seed of M3's adversarial test suite — every case there encodes a decision the real
statement guard must make, with a note on what it is probing. That file graduates to `internal/guard/testdata`;
the rest goes in the bin.

## Running it

No local Go toolchain needed:

```bash
docker run --rm -v "$PWD:/work" -w /work golang:1-bookworm bash -c '
  CGO_ENABLED=0 go run ./cmd/pg &&        # PostgreSQL corpus, 35 cases
  CGO_ENABLED=0 go run ./cmd/tsql &&      # SQL Server corpus, 19 cases
  CGO_ENABLED=0 go run ./cmd/robust &&    # does an unknown statement fail closed?
  CGO_ENABLED=0 go run ./cmd/failopen &&  # does the parser silently drop input?
  CGO_ENABLED=0 go run ./cmd/holes'       # function calls and INSERT ... EXEC
```

`cmd/pg` and `cmd/tsql` exit non-zero on any corpus failure, so they work as a regression check.

## What each command answers

| Command | Question |
|---|---|
| `pg` | Can libpg_query-via-wasm answer every question the guard must ask, with `CGO_ENABLED=0`? |
| `tsql` | Can a pure-Go T-SQL parser do the same? |
| `probe` | What does teesql's AST actually look like? (written before the classifier, so it was built against reality rather than docs) |
| `robust` | On unfamiliar syntax, does the parser error — or hand back a tree the guard would trust? |
| `failopen` | When it does not error, does it drop the input it could not parse? |
| `holes` | Are `INSERT ... EXEC` and dangerous function calls visible in the tree? |

The classifiers in `cmd/pg` and `cmd/tsql` are written the way the real guard should be — allow-list of statement
types, exhaustive tree walk rather than a top-level type switch, and predicate analysis rather than a
WHERE-presence test — so that a corpus pass means something.
