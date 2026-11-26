#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PluginsDir = (Join-Path $env:APPDATA 'JetBrains\Rider2026.3\plugins'),
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$bundleDir = Join-Path $repoRoot 'bundle\rider-vcx-filters-fix'
$pluginId = 'local.rider.vcx-filter-view-fix'
$allowedFiles = @('lib/vcx-filter-view-fix.jar', 'dotnet/RiderVcxFix.Backend.dll', 'dotnet/0Harmony.dll')

function Assert-NoRiderProcesses {
    $running = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '(?i)^(rider64|.*Rider.*Backend.*|.*Lightweight.*)\.exe$' -or
        ($_.Name -match '(?i)^dotnet\.exe$' -and $_.CommandLine -match '(?i)(Rider[.\\/]BackendHost|LightweightBackend|ReSharperHost)')
    })
    if ($running.Count -gt 0) {
        $desc = ($running | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }) -join ', '
        throw "Close all Rider frontend/backend processes before installing: $desc"
    }
}

function Get-JarPluginIdentity([string]$JarPath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($JarPath)
    try {
        $entry = $archive.GetEntry('META-INF/plugin.xml')
        if (-not $entry) { return $null }
        $reader = [System.IO.StreamReader]::new($entry.Open())
        try { [xml]$doc = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $idNode = $doc.SelectSingleNode('/idea-plugin/id')
        $verNode = $doc.SelectSingleNode('/idea-plugin/version')
        if (-not $idNode) { return $null }
        [pscustomobject]@{ Id = $idNode.InnerText.Trim(); Version = $(if ($verNode) { $verNode.InnerText.Trim() } else { '' }) }
    } finally { $archive.Dispose() }
}

if (-not (Test-Path -LiteralPath $bundleDir -PathType Container)) {
    throw "Bundle directory not found: $bundleDir. Run backend/build.ps1 and frontend/build.ps1 first."
}

$bundleFiles = @(Get-ChildItem -LiteralPath $bundleDir -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($bundleDir.Length + 1).Replace('\', '/')
    [pscustomobject]@{
        RelativePath = $rel
        FullName = $_.FullName
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        Length = $_.Length
    }
})
$diff = @(Compare-Object -ReferenceObject $allowedFiles -DifferenceObject @($bundleFiles.RelativePath))
if ($diff.Count -gt 0) {
    throw "Bundle does not contain the exact required runtime files: $($allowedFiles -join ', ')"
}

$sourceIdentity = Get-JarPluginIdentity (Join-Path $bundleDir 'lib\vcx-filter-view-fix.jar')
if (-not $sourceIdentity -or $sourceIdentity.Id -ne $pluginId) {
    throw "Unexpected plugin identity in bundle JAR."
}

if (-not (Test-Path -LiteralPath $PluginsDir -PathType Container)) {
    throw "Rider plugins directory not found: $PluginsDir"
}

# Find any existing installations of the same plugin ID.
$existingMatches = @()
foreach ($dir in Get-ChildItem -LiteralPath $PluginsDir -Directory) {
    $jars = @(Get-ChildItem -LiteralPath $dir.FullName -Filter '*.jar' -File -ErrorAction SilentlyContinue)
    $libDir = Join-Path $dir.FullName 'lib'
    if (Test-Path -LiteralPath $libDir -PathType Container) {
        $jars += @(Get-ChildItem -LiteralPath $libDir -Filter '*.jar' -File -ErrorAction SilentlyContinue)
    }
    foreach ($jar in $jars) {
        $id = Get-JarPluginIdentity $jar.FullName
        if ($id -and $id.Id -eq $pluginId) {
            $existingMatches += [pscustomobject]@{ Path = $dir.FullName; Version = $id.Version }
            break
        }
    }
}

$targetDir = Join-Path $PluginsDir 'rider-vcx-filters-fix'
$plan = [ordered]@{
    PluginId = $sourceIdentity.Id
    NewVersion = $sourceIdentity.Version
    SourceBundle = $bundleDir
    TargetDir = $targetDir
    ExistingInstallations = @($existingMatches)
    Files = @($bundleFiles | Select-Object RelativePath, Sha256, Length)
}

if ($VerifyOnly) {
    $plan | ConvertTo-Json -Depth 6
    return
}

Assert-NoRiderProcesses

if ($existingMatches.Count -gt 0) {
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $backupRoot = Join-Path $repoRoot ".local-archive\backups\install-$stamp"
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    foreach ($match in $existingMatches) {
        $destBackup = Join-Path $backupRoot (Split-Path -Leaf $match.Path)
        Move-Item -LiteralPath $match.Path -Destination $destBackup
        Write-Host "Backed up previous version ($($match.Version)) -> $destBackup"
    }
}

foreach ($f in $bundleFiles) {
    $dest = Join-Path $targetDir ($f.RelativePath.Replace('/', '\'))
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
}

Write-Host "Installed $($sourceIdentity.Id) $($sourceIdentity.Version) -> $targetDir"
$plan | ConvertTo-Json -Depth 6
