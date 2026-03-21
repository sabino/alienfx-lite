[CmdletBinding()]
param()

$serviceName = 'AlienFxLiteService'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell window.'
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "$serviceName is not installed."
    return
}

$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
$binaryPath = if ($service.PathName -match '^\s*"([^"]+)"') {
    $Matches[1]
} elseif ($service.PathName -match '^\s*([^\s]+)') {
    $Matches[1]
} else {
    throw "Unable to resolve the installed binary path from service command line: $($service.PathName)"
}

& $binaryPath --uninstall-service
if ($LASTEXITCODE -ne 0) {
    throw "AlienFx Lite service uninstall failed with exit code $LASTEXITCODE."
}

Write-Host "Removed $serviceName"
