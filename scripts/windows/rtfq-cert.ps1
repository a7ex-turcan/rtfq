#Requires -Version 7

<#
.SYNOPSIS
    Generates the self-signed certificate RTFQ needs to listen beyond loopback.

.DESCRIPTION
    Needs PowerShell 7. The key export used here (ExportPkcs8PrivateKey) is a
    .NET Core API and does not exist in Windows PowerShell 5.1, so run this with
    `pwsh`, not `powershell`. If PS7 is not installed:

        winget install Microsoft.PowerShell

    RTFQ reads PEM files by path, so this exports rather than leaving the cert
    in the Windows store.

    The subject alternative name is the part that matters: clients validate
    against it, so it must list the name or address agents will actually dial.
    A certificate for COMPUTERNAME is useless to somebody connecting by IP.

    Self-signed means callers need --insecure-skip-verify until you install this
    cert in their trust store. That flag is client-side only: it cannot weaken
    anything the server enforces, but the client does stop checking who it is
    talking to.

.EXAMPLE
    pwsh .\rtfq-cert.ps1 -IpAddress 10.0.4.17

.EXAMPLE
    pwsh .\rtfq-cert.ps1 -DnsName rtfq.qa.internal -Days 730
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$DnsName = @($env:COMPUTERNAME),
    [string[]]$IpAddress = @(),
    [string]$OutDir = 'C:\ProgramData\rtfq',
    [ValidateRange(1, 3650)]
    [int]$Days = 365
)

$ErrorActionPreference = 'Stop'

$names = @($DnsName | Where-Object { $_ }) + $IpAddress | Select-Object -Unique
if (-not $names) { throw 'No names to certify. Pass -DnsName or -IpAddress.' }

if (-not $PSCmdlet.ShouldProcess($OutDir, "Write tls.crt and tls.key for $($names -join ', ')")) { return }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$certPath = Join-Path $OutDir 'tls.crt'
$keyPath = Join-Path $OutDir 'tls.key'

Write-Host "Certifying: $($names -join ', ')"

$cert = New-SelfSignedCertificate `
    -Subject "CN=$($names[0])" `
    -DnsName $names `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -NotAfter (Get-Date).AddDays($Days)

try {
    $certB64 = [Convert]::ToBase64String($cert.RawData, 'InsertLineBreaks')
    $keyB64 = [Convert]::ToBase64String($cert.PrivateKey.ExportPkcs8PrivateKey(), 'InsertLineBreaks')

    Set-Content $certPath -Encoding ascii -Value @(
        '-----BEGIN CERTIFICATE-----'
        $certB64
        '-----END CERTIFICATE-----'
    )
    Set-Content $keyPath -Encoding ascii -Value @(
        '-----BEGIN PRIVATE KEY-----'
        $keyB64
        '-----END PRIVATE KEY-----'
    )
}
finally {
    # The files are the artifact. A copy left in the store is a second thing to
    # rotate and a second thing to forget.
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue
}

# The private key should not be readable by everyone just because ProgramData is.
$acl = Get-Acl $keyPath
$acl.SetAccessRuleProtection($true, $false)
$acl.Access | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
foreach ($who in 'BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM') {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($who, 'FullControl', 'Allow')))
}
Set-Acl $keyPath $acl

$expires = (Get-Date).AddDays($Days).ToString('yyyy-MM-dd')
$thumb = (Get-FileHash $certPath -Algorithm SHA256).Hash

Write-Host ''
Write-Host "  $certPath"
Write-Host "  $keyPath   (Administrators and SYSTEM only)"
Write-Host "  expires    $expires"
Write-Host "  sha256     $thumb"
Write-Host ''
Write-Host 'Put this in rtfq.yaml. Paths, not ${file:...} references:' -ForegroundColor Cyan
Write-Host ''
Write-Host 'server:'
Write-Host '  listen: 0.0.0.0:7420'
Write-Host '  tls:'
Write-Host "    cert: $certPath"
Write-Host "    key: $keyPath"
Write-Host ''
Write-Host 'Then:' -ForegroundColor Cyan
Write-Host "  rtfq validate --config $OutDir\rtfq.yaml --production"
Write-Host '  .\rtfq-firewall.ps1'
Write-Host ''
Write-Host 'Clients, until this cert is in their trust store:' -ForegroundColor Cyan
Write-Host "  rtfq sources --server https://$($names[0]):7420 --token YOURS --insecure-skip-verify"
