[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir
)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'artifacts\release'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
    $Version = $props.Project.PropertyGroup.Version
}

$plainVersion = $Version.Trim().TrimStart('v', 'V')
$stagingRoot = Join-Path $repoRoot 'artifacts\staging'
$publishDir = Join-Path $stagingRoot 'app'
$portableRoot = Join-Path $stagingRoot 'portable\AlienFxLite'
$toolPublishDir = Join-Path $stagingRoot 'tool'
$toolBundleRoot = Join-Path $stagingRoot 'tool-bundle\AlienFxLite.Tool'
$desktopArtifactDir = Join-Path $repoRoot 'artifacts\app'
$toolArtifactDir = Join-Path $repoRoot 'artifacts\tool'

foreach ($path in @($stagingRoot, $OutputDir, $desktopArtifactDir, $toolArtifactDir)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

if ($pythonCommand = Get-Command python -ErrorAction SilentlyContinue) {
    $python = $pythonCommand.Source
}
else {
    $python = $null
}
if (-not $python) {
    if ($pyCommand = Get-Command py -ErrorAction SilentlyContinue) {
        $python = $pyCommand.Source
    }
    if (-not $python) {
        throw "Python was not found. Install Python 3 to generate the AlienFx Lite icon assets."
    }

    & $python -3 .\scripts\generate_icons.py
}
else {
    & $python .\scripts\generate_icons.py
}

if ($LASTEXITCODE -ne 0) {
    throw "Icon generation failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'build-native-bridge.ps1') -Configuration $Configuration

dotnet publish .\AlienFxLite.UI\AlienFxLite.UI.csproj `
    -c $Configuration `
    -r $Runtime `
    -p:Version=$plainVersion `
    -p:InformationalVersion=$plainVersion `
    -p:PublishSingleFile=true `
    -p:SelfContained=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "UI publish failed with exit code $LASTEXITCODE."
}

dotnet publish .\AlienFxLite.Tool\AlienFxLite.Tool.csproj `
    -c $Configuration `
    -r $Runtime `
    -p:Version=$plainVersion `
    -p:InformationalVersion=$plainVersion `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    --self-contained false `
    -o $toolPublishDir

if ($LASTEXITCODE -ne 0) {
    throw "Tool publish failed with exit code $LASTEXITCODE."
}

Copy-Item (Join-Path $publishDir '*') $desktopArtifactDir -Recurse -Force
Copy-Item (Join-Path $toolPublishDir '*') $toolArtifactDir -Recurse -Force

New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
New-Item -ItemType Directory -Path $toolBundleRoot -Force | Out-Null

Copy-Item (Join-Path $publishDir '*') $portableRoot -Recurse -Force
Copy-Item .\README.md, .\LICENSE, .\CHANGELOG.md $portableRoot -Force
Copy-Item (Join-Path $toolPublishDir '*') $toolBundleRoot -Recurse -Force
Copy-Item .\README.md, .\LICENSE, .\CHANGELOG.md $toolBundleRoot -Force

$portableZip = Join-Path $OutputDir "AlienFxLite-portable-win-x64-v$plainVersion.zip"
$toolZip = Join-Path $OutputDir "AlienFxLite.Tool-win-x64-v$plainVersion.zip"

Compress-Archive -Path (Join-Path $portableRoot '*') -DestinationPath $portableZip -CompressionLevel Optimal
Compress-Archive -Path (Join-Path $toolBundleRoot '*') -DestinationPath $toolZip -CompressionLevel Optimal

& (Join-Path $PSScriptRoot 'build-installer.ps1') -Version $plainVersion -PublishDir $publishDir -OutputDir $OutputDir

$setupArtifact = Get-ChildItem $OutputDir "AlienFxLite-Setup-win-x64-v$plainVersion.exe" | Select-Object -First 1
if ($setupArtifact -and $setupArtifact.Length -gt 15MB) {
    $sizeMb = [math]::Round($setupArtifact.Length / 1MB, 2)
    throw "Installer size gate exceeded: $sizeMb MB (limit 15 MB)."
}

$checksumFile = Join-Path $OutputDir 'SHA256SUMS.txt'
Get-ChildItem $OutputDir -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash $_.FullName -Algorithm SHA256
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), $_.Name
    } | Set-Content -Path $checksumFile -Encoding ASCII

Write-Host "Release artifacts created in $OutputDir"
