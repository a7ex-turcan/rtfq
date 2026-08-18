<#
.SYNOPSIS
    Opens (or closes) the inbound port RTFQ listens on.

.DESCRIPTION
    Only useful once RTFQ is listening beyond loopback. Opening the port while
    the config still says 127.0.0.1 achieves nothing, and moving off loopback
    requires TLS — `rtfq validate` refuses `0.0.0.0` without a certificate, and
    there is no override. So the order is: certificate, config, then this.

    Defaults to LocalSubnet rather than Any. A bearer token is the only thing
    between this port and four databases, and a token is one leaked environment
    variable away from being public — so the smaller the set of machines that
    can reach it, the better. Pass -RemoteAddress explicitly to widen it.

.EXAMPLE
    .\rtfq-firewall.ps1
    Allows inbound TCP 7420 from the local subnet.

.EXAMPLE
    .\rtfq-firewall.ps1 -RemoteAddress 10.0.4.0/24,10.0.5.17
    Allows only those callers.

.EXAMPLE
    .\rtfq-firewall.ps1 -WhatIf
    Shows what would change without touching anything.

.EXAMPLE
    .\rtfq-firewall.ps1 -Remove
    Deletes the rule.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidateRange(1, 65535)]
    [int]$Port = 7420,

    [string[]]$RemoteAddress = @('LocalSubnet'),

    [string]$RuleName = 'RTFQ',

    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

# Firewall changes need elevation, and the failure without it is a confusing
# access-denied from deep inside the cmdlet. Say so plainly instead.
$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This needs an elevated PowerShell. Right-click > Run as Administrator, then run it again."
}

$existing = Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue

if ($Remove) {
    if (-not $existing) {
        Write-Host "No rule named '$RuleName'. Nothing to remove."
        return
    }
    if ($PSCmdlet.ShouldProcess($RuleName, 'Remove firewall rule')) {
        $existing | Remove-NetFirewallRule
        Write-Host "Removed '$RuleName'. The port is closed again."
    }
    return
}

# Idempotent: re-running with different arguments should reconfigure the rule
# rather than stack a second one beside it with different scope.
if ($existing) {
    if ($PSCmdlet.ShouldProcess($RuleName, "Update to TCP $Port from $($RemoteAddress -join ', ')")) {
        $existing | Set-NetFirewallRule -LocalPort $Port -RemoteAddress $RemoteAddress -Protocol TCP -Action Allow -Enabled True
        Write-Host "Updated '$RuleName'."
    }
}
elseif ($PSCmdlet.ShouldProcess($RuleName, "Allow inbound TCP $Port from $($RemoteAddress -join ', ')")) {
    New-NetFirewallRule -DisplayName $RuleName `
        -Description 'RTFQ — governed access to configured data sources' `
        -Direction Inbound -Protocol TCP -LocalPort $Port `
        -RemoteAddress $RemoteAddress -Action Allow -Profile Domain, Private | Out-Null
    Write-Host "Created '$RuleName'."
}

# Deliberately not Public: a laptop on hotel wi-fi should not be serving this.
Get-NetFirewallRule -DisplayName $RuleName |
    Format-List DisplayName, Enabled, Direction, Action, Profile

Get-NetFirewallRule -DisplayName $RuleName |
    Get-NetFirewallAddressFilter |
    Format-List @{ n = 'RemoteAddress'; e = { $_.RemoteAddress -join ', ' } }

Write-Host ""
Write-Host "The port is open. It is only reachable if rtfq.yaml listens beyond loopback:"
Write-Host "    listen: 0.0.0.0:$Port"
Write-Host "which requires a certificate. Check before you rely on it:"
Write-Host "    rtfq validate --config C:\ProgramData\rtfq\rtfq.yaml --production"
