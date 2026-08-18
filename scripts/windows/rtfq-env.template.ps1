<#
.SYNOPSIS
    Template: sets the environment variables an rtfq.yaml references.

.DESCRIPTION
    RTFQ never stores credentials. The config says ${env:ORDERS_DSN} and the
    value has to be in the environment of whatever process runs `rtfq serve`.
    This is one way to put it there on Windows.

    COPY THIS FILE, fill in the $vars block, and keep your copy out of version
    control. The names in $vars must match the ${env:...} references in your
    rtfq.yaml — they are names your config chooses, not names RTFQ knows.

    Persists to the User environment by default, and sets the current session
    too, so `rtfq serve` works now and after a reboot.

        .\rtfq-env.ps1

    Use -Machine if RTFQ runs as a Windows service or under another account. A
    service does NOT see your user environment, and that is the usual reason
    variables look set but the server cannot resolve them.

        .\rtfq-env.ps1 -Machine        # elevated

    -Process sets them for this session only and writes nothing to disk.

    Persisting puts credentials in the registry, readable by anything running
    as that user — or by everything, at Machine scope. That is the trade for
    surviving a reboot. If it is not one you want, use -Process, or reference a
    secret store from your config instead.

.PARAMETER Machine
    Persist machine-wide rather than per-user. Needs an elevated PowerShell.

.PARAMETER Process
    Session only. Nothing is written to disk.

.PARAMETER Remove
    Delete these variables from the chosen scope.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Machine,
    [switch]$Process,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

if ($Machine -and $Process) { throw 'Pick one of -Machine or -Process.' }
$scope = if ($Process) { 'Process' } elseif ($Machine) { 'Machine' } else { 'User' }

if ($Machine) {
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Machine scope needs an elevated PowerShell. Run as Administrator, or drop -Machine.'
    }
}

# ---------------------------------------------------------------------------
# FILL THIS IN. Single-quoted throughout: connection strings routinely contain
# $ ; ! and #, and in double quotes PowerShell would act on some of them.
# ---------------------------------------------------------------------------
$vars = [ordered]@{
    # The bearer token agents present to RTFQ. Nothing to do with any database
    # — you invent it. Generate one with:
    #   -join ((1..32) | ForEach-Object { '{0:x2}' -f (Get-Random -Max 256) })
    RTFQ_AGENT_SECRET = 'change-me'

    # SQL Server
    CRM_DSN = 'Server=sql-a.internal,1433;Database=Crm;User Id=rtfq_ro;Password=CHANGE-ME;TrustServerCertificate=True;'

    # PostgreSQL
    REFERENCE_DSN = 'Host=pg.internal;Port=5432;Database=Reference;Username=rtfq_ro;Password=CHANGE-ME;SSL Mode=Require;Trust Server Certificate=true;'
}
# ---------------------------------------------------------------------------

$action = if ($Remove) { "Remove from $scope environment" } else { "Write to $scope environment" }
if (-not $PSCmdlet.ShouldProcess(($vars.Keys -join ', '), $action)) { return }

foreach ($name in $vars.Keys) {
    $value = if ($Remove) { $null } else { $vars[$name] }

    # Always fix the current session, so this shell can run rtfq immediately.
    if ($null -eq $value) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    } else {
        Set-Item -Path "Env:$name" -Value $value
    }

    # And persist, unless asked not to. Process scope is the session, done above.
    if ($scope -ne 'Process') {
        [Environment]::SetEnvironmentVariable($name, $value, $scope)
    }
}

# Read back from the store rather than trusting the write. This is what turns
# "somehow it didn't work" into a specific answer.
Write-Host ''
Write-Host "Scope: $scope" -ForegroundColor Cyan
$rows = foreach ($name in $vars.Keys) {
    $stored = [Environment]::GetEnvironmentVariable($name, $scope)
    [pscustomobject]@{
        Variable  = $name
        Session   = if ([Environment]::GetEnvironmentVariable($name, 'Process')) { 'set' } else { '-' }
        Persisted = if ($scope -eq 'Process') { 'n/a' } elseif ($stored) { "$($stored.Length) chars" } else { '-' }
    }
}
# Lengths, not values: a connection string printed here ends up in scrollback,
# a screenshot, or a CI log.
$rows | Format-Table -AutoSize

if ($Remove) { Write-Host 'Removed.'; return }

if ($vars['RTFQ_AGENT_SECRET'] -eq 'change-me') {
    Write-Warning "RTFQ_AGENT_SECRET is still 'change-me'. Anyone who can reach the port can use it."
}

Write-Host 'Terminals already open will not see the persisted values until restarted.'
if ($scope -eq 'User') {
    Write-Host 'If rtfq runs as a Windows service, re-run with -Machine: a service does' -ForegroundColor Yellow
    Write-Host 'not see your user environment.' -ForegroundColor Yellow
}
