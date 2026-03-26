[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PreviousTag
)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

$plainVersion = $Version.Trim().TrimStart('v', 'V')
$parsedVersion = $null
if (-not [Version]::TryParse($plainVersion, [ref]$parsedVersion)) {
    throw "Version '$Version' is not a valid semantic version."
}

[xml]$props = Get-Content (Join-Path $repoRoot 'Directory.Build.props')
$props.Project.PropertyGroup.Version = $plainVersion
$props.Project.PropertyGroup.AssemblyVersion = "$plainVersion.0"
$props.Project.PropertyGroup.FileVersion = "$plainVersion.0"
$props.Project.PropertyGroup.InformationalVersion = $plainVersion
$props.Save((Join-Path $repoRoot 'Directory.Build.props'))

$date = Get-Date -Format 'yyyy-MM-dd'
$note = if ([string]::IsNullOrWhiteSpace($PreviousTag)) {
    '- Automated release build from `main`.'
}
else {
    "- Automated release build from `main`, covering changes since `$PreviousTag`."
}

$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$changelog = Get-Content $changelogPath -Raw
$entry = "## $plainVersion - $date`r`n`r`n$note`r`n`r`n"

if ($changelog -match '^\# Changelog\r?\n\r?\n') {
    $updated = [System.Text.RegularExpressions.Regex]::Replace(
        $changelog,
        '^\# Changelog\r?\n\r?\n',
        "# Changelog`r`n`r`n$entry",
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
}
else {
    $updated = "# Changelog`r`n`r`n$entry$changelog"
}

Set-Content -Path $changelogPath -Value $updated -Encoding UTF8
