# Setup scripts

Nothing here is required to run RTFQ — the binary needs a config file and an
environment, and how those arrive is your business. These exist because the same
four chores come up every time on Windows, and getting one of them subtly wrong
produces a failure that looks like something else entirely.

They came out of an actual deployment rather than being written to look tidy, so
the comments explain the traps as much as the steps.

## windows/

| Script | What it does |
|---|---|
| `rtfq-path.ps1` | Puts `rtfq.exe` on PATH so `rtfq` works anywhere. |
| `rtfq-env.template.ps1` | **Copy me.** Sets the `${env:...}` values your config references. |
| `rtfq-cert.ps1` | Self-signed certificate, needed to listen beyond loopback. |
| `rtfq-firewall.ps1` | Opens the inbound port, scoped to the local subnet by default. |

Every one supports `-WhatIf`. All are idempotent.

### Order

On the machine that can reach the databases:

```powershell
cd C:\tools\rtfq

.\rtfq-path.ps1                                    # 1. rtfq on PATH

copy .\rtfq-env.template.ps1 .\rtfq-env.ps1        # 2. credentials
#    edit rtfq-env.ps1 to match your config's ${env:...} names
.\rtfq-env.ps1

rtfq validate --config .\rtfq.yaml                 # 3. does it load?
rtfq serve    --config .\rtfq.yaml
```

Only if agents connect from another machine:

```powershell
pwsh .\rtfq-cert.ps1 -IpAddress 10.0.0.5           # 4. certificate
#    then set listen: 0.0.0.0:7420 and the tls: block in rtfq.yaml
rtfq validate --config .\rtfq.yaml --production
.\rtfq-firewall.ps1                                # 5. open the port (elevated)
```

And on each client:

```powershell
$env:RTFQ_SERVER = 'https://10.0.0.5:7420'
$env:RTFQ_TOKEN  = '<the RTFQ_AGENT_SECRET value>'
rtfq sources --insecure-skip-verify
```

## Things that cost us time

**`rtfq-cert.ps1` needs PowerShell 7.** It exports the private key with
`ExportPkcs8PrivateKey()`, a .NET Core API that does not exist in Windows
PowerShell 5.1. Run it with `pwsh`; the script declares `#Requires -Version 7`
so 5.1 refuses it up front instead of failing halfway through. The other three
run on 5.1. If PS7 is not an option, generate the pair with `openssl` instead —
RTFQ only wants two PEM files.

**`tls.cert` and `tls.key` take paths, not `${file:...}` references.** They are
the one exception to *secrets are referenced, never inlined*, because RTFQ hands
the paths to the TLS stack rather than reading them. Writing `${file:...}` there
substitutes the PEM itself and then reports it as a missing filename.

**A Windows service does not see your user environment.** If `rtfq serve` runs
as a service and cannot resolve `${env:...}` while the variables look perfectly
set in your shell, that is why — re-run the env script with `-Machine`.

**Certificate SANs must list what callers actually dial.** A certificate for
`COMPUTERNAME` is useless to somebody connecting by IP. Pass `-IpAddress`,
`-DnsName`, or both.

**Check the account running RTFQ can read `tls.key`.** `rtfq-cert.ps1` locks it
to Administrators and SYSTEM. Correct for an elevated console or a SYSTEM
service; wrong for a dedicated service account, where it surfaces as a startup
failure that reads like a bad certificate.

## Not on Windows?

The README covers the same ground for Linux and macOS: `install -m 0755` for
PATH, `openssl req -x509` for the certificate, `ufw` or `firewall-cmd` for the
port. Nothing here is doing anything a shell one-liner could not.
