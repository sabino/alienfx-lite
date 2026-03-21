[CmdletBinding()]
param(
    [string]$BinaryPath = (Join-Path $PSScriptRoot '..\artifacts\service\AlienFxLite.Service.exe'),
    [string]$AllowedUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
)

$serviceName = 'AlienFxLiteService'
$displayName = 'AlienFx Lite Service'
$configRoot = Join-Path $env:ProgramData 'AlienFxLite'
$configPath = Join-Path $configRoot 'service.json'

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

New-Item -ItemType Directory -Path $configRoot -Force | Out-Null

@{
    allowedUserSid = $AllowedUserSid
} | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
    }

    sc.exe delete $serviceName | Out-Null
    do {
        Start-Sleep -Milliseconds 250
        $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    } while ($existing)
}

New-Service -Name $serviceName -BinaryPathName "`"$resolvedBinaryPath`"" -DisplayName $displayName -Description 'AlienFx Lite privileged broker for Dell fan and lighting control.' -StartupType Automatic
sc.exe config $serviceName obj= LocalSystem start= delayed-auto | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/60000 | Out-Null
Start-Service -Name $serviceName

$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"

Write-Host "Installed $displayName"
Write-Host "Binary:  $resolvedBinaryPath"
Write-Host "Account: $($service.StartName)"
Write-Host "Pipe SID: $AllowedUserSid"
