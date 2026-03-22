[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Platform = 'x64'
)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'AlienFxLite.NativeBridge\AlienFxLite.NativeBridge.vcxproj'

$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vsWhere)) {
    throw "vswhere.exe was not found. Install Visual Studio Build Tools with MSVC support."
}

$installPath = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if ([string]::IsNullOrWhiteSpace($installPath)) {
    throw "Unable to locate a Visual Studio/MSBuild installation for the native bridge build."
}

$msbuildPath = Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path $msbuildPath)) {
    throw "MSBuild.exe was not found at '$msbuildPath'."
}

& $msbuildPath $projectPath /p:Configuration=$Configuration /p:Platform=$Platform /m
if ($LASTEXITCODE -ne 0) {
    throw "Native bridge build failed with exit code $LASTEXITCODE."
}
