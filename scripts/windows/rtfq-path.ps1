<#
.SYNOPSIS
    Puts rtfq.exe on your PATH so `rtfq` works from any directory.

.DESCRIPTION
    Defaults to the folder this script is sitting in, so drop it next to
    rtfq.exe and run it with no arguments.

    User scope by default — no elevation needed. Use -Machine if rtfq will run
    as a Windows service or under another account, since a service does not see
    your user PATH.

.EXAMPLE
    .\rtfq-path.ps1

.EXAMPLE
    .\rtfq-path.ps1 -Directory C:\tools\rtfq -Machine

.EXAMPLE
    .\rtfq-path.ps1 -Remove
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Directory = $PSScriptRoot,
    [switch]$Machine,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

$scope = if ($Machine) { 'Machine' } else { 'User' }
if (-not $Directory) { throw 'Pass -Directory, or run this from the folder holding rtfq.exe.' }
if (-not (Test-Path -PathType Container $Directory)) { throw "No such folder: $Directory" }
$Directory = (Resolve-Path $Directory).Path.TrimEnd('\')

if ($Machine) {
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Machine scope needs an elevated PowerShell. Run as Administrator, or drop -Machine for user scope.'
    }
}

$exe = Join-Path $Directory 'rtfq.exe'
if (-not $Remove -and -not (Test-Path $exe)) {
    throw "No rtfq.exe in $Directory. Pass -Directory with the folder that has it."
}

# Read the raw stored value. [Environment]::GetEnvironmentVariable with a scope
# gives the stored PATH rather than the merged one in $env:Path, which is what
# has to be written back.
$current = [Environment]::GetEnvironmentVariable('Path', $scope)
$entries = @($current -split ';' | Where-Object { $_ })
$present = $entries | Where-Object { $_.TrimEnd('\') -ieq $Directory }

if ($Remove) {
    if (-not $present) { Write-Host "$Directory is not on the $scope PATH."; return }
    if ($PSCmdlet.ShouldProcess($Directory, "Remove from $scope PATH")) {
        $kept = $entries | Where-Object { $_.TrimEnd('\') -ine $Directory }
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), $scope)
        Write-Host "Removed $Directory from the $scope PATH."
    }
    return
}

if ($present) {
    Write-Host "$Directory is already on the $scope PATH."
}
elseif ($PSCmdlet.ShouldProcess($Directory, "Append to $scope PATH")) {
    [Environment]::SetEnvironmentVariable('Path', (($entries + $Directory) -join ';'), $scope)
    Write-Host "Added $Directory to the $scope PATH."
}

# Persisted PATH only reaches processes started afterwards, so patch this
# session too rather than telling you to reopen the terminal.
if ($env:Path -notlike "*$Directory*") { $env:Path += ";$Directory" }

Write-Host ''
if (Get-Command rtfq -ErrorAction SilentlyContinue) {
    Write-Host "rtfq resolves to: $((Get-Command rtfq).Source)"
    & rtfq --version
} else {
    Write-Warning 'rtfq still does not resolve in this session.'
}
Write-Host ''
Write-Host 'Terminals already open will not see this until they are restarted.'
