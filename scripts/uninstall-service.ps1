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

if ($existing.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force -ErrorAction Stop
}

sc.exe delete $serviceName | Out-Null
Write-Host "Removed $serviceName"
