# Parser spike — throwaway

Evidence for [ADR 0001](../../docs/decisions/0001-sql-parser-selection.md). **Not part of the RTFQ build**: its
own project, no relationship to `src/`, and it will be deleted once M3 lands.

It is kept in the repo because the ADR's claims should be re-runnable when a dependency is bumped, and because
`Corpus.cs` is the seed of M3's adversarial test suite — every case encodes a decision the real statement guard
must make, with a note on what it probes. That file graduates to the guard's test project; the rest goes in the bin.

## Running it

```bash
dotnet run -- pg        # PostgreSQL corpus, 35 cases   (Npgquery / libpg_query)
dotnet run -- tsql      # SQL Server corpus, 20 cases   (Microsoft ScriptDom)
dotnet run -- anomaly   # why libpg_query accepts "SELECT 1 @@@@ DELETE FROM orders"
dotnet run -- probe     # Npgquery's real API surface, by reflection
```

`pg` and `tsql` exit non-zero on any corpus failure, so they work as a regression check.

## Run it against the AOT binary, not just the JIT build

This is the point of the spike, not a footnote. The T-SQL classifier originally walked the AST by reflection: it
scored **20/20 under the JIT and 3/20 as a published NativeAOT binary**, with every failure in the unsafe
direction — `DROP TABLE`, `TRUNCATE` and `EXEC xp_cmdshell` all classified as harmless reads, because the trimmer
had removed the property metadata the walk depended on.

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  apt-get update -qq && apt-get install -y -qq clang zlib1g-dev &&
  dotnet publish -c Release -r linux-x64 /p:PublishAot=true -o /out &&
  /out/spike pg && /out/spike tsql'
```

## What each command answers

| Command | Question |
|---|---|
| `pg` | Can libpg_query-via-P/Invoke answer every question the guard must ask? |
| `tsql` | Can Microsoft's own ScriptDom do the same, and does it fail closed? |
| `anomaly` | libpg_query accepted a garbage-looking string — did it drop the tail, or is the string genuinely one statement? |
| `probe` | What is Npgquery's actual API? (written before the classifier, so it was built against reality rather than docs) |

The classifiers are written the way the real guard should be — allow-list of statement types, exhaustive walk via
the parser's own visitor rather than reflection, and predicate analysis rather than a WHERE-presence test — so
that a corpus pass means something.
