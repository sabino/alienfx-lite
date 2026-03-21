[CmdletBinding()]
param(
    [string]$BinaryPath = (Join-Path $PSScriptRoot '..\artifacts\app\AlienFxLite.exe'),
    [string]$AllowedUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
)

$serviceName = 'AlienFxLiteService'
$displayName = 'AlienFx Lite Service'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}

$resolvedBinaryPath = (Resolve-Path $BinaryPath).Path
if (-not (Test-Path $resolvedBinaryPath -PathType Leaf)) {
    throw "Binary not found: $BinaryPath"
}

if ([string]::IsNullOrWhiteSpace($AllowedUserSid)) {
    throw 'AllowedUserSid cannot be empty.'
}

& $resolvedBinaryPath --install-service --binary-path $resolvedBinaryPath --allowed-user-sid $AllowedUserSid
if ($LASTEXITCODE -ne 0) {
    throw "AlienFx Lite service install failed with exit code $LASTEXITCODE."
}

$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"

Write-Host "Installed $displayName"
Write-Host "Binary:  $resolvedBinaryPath"
Write-Host "Account: $($service.StartName)"
Write-Host "Pipe SID: $AllowedUserSid"
