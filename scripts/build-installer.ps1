[CmdletBinding()]
param(
    [string]$Version,
    [string]$PublishDir,
    [string]$OutputDir
)

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version is required.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $repoRoot 'artifacts\staging\app'
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'artifacts\release'
}

$resolvedPublishDir = (Resolve-Path $PublishDir).Path

if (-not (Test-Path $resolvedPublishDir -PathType Container)) {
    throw "Publish directory not found: $PublishDir"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$candidatePaths = @(
    $(if ($command = Get-Command iscc.exe -ErrorAction SilentlyContinue) { $command.Source }),
    $(if ($command = Get-Command iscc -ErrorAction SilentlyContinue) { $command.Source }),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$isccPath = $candidatePaths | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1

if (-not $isccPath) {
    throw 'Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup 6 first.'
}

$scriptPath = (Resolve-Path (Join-Path $PSScriptRoot 'installer\AlienFxLite.iss')).Path
& $isccPath "/DAppVersion=$Version" "/DPublishDir=$resolvedPublishDir" "/DOutputDir=$OutputDir" "/DRepoRoot=$repoRoot" $scriptPath

if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}
