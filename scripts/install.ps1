[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-FullPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-UnderRoot([string]$Path, [string]$Root) {
    $full = Get-FullPath $Path
    $base = Get-FullPath $Root
    if (-not $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the exact allowed root: $full (root $base)"
    }
}

function Assert-PlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing a junction/symlink/reparse path: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($parent) { $parent.FullName } else { $null }
    }
}

function Assert-NoRiderProcesses {
    $blocked = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '(?i)^(rider64|.*Rider.*Backend.*|.*Lightweight.*)\.exe$' -or
        ($_.Name -match '(?i)^dotnet\.exe$' -and $_.CommandLine -match '(?i)(Rider[.\\/]BackendHost|LightweightBackend|ReSharperHost)')
    })
    if ($blocked.Count) {
        $names = $blocked | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }
        throw ('Close every Rider frontend/backend/lightweight instance before installation: ' + ($names -join ', '))
    }
}

function Read-PluginXml([string]$Text) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($Text), $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    $id = $document.SelectSingleNode('/idea-plugin/id')
    if (-not $id) { return $null }
    $version = $document.SelectSingleNode('/idea-plugin/version')
    [pscustomobject]@{ Id = $id.InnerText.Trim(); Version = $(if ($version) { $version.InnerText.Trim() } else { '' }); Document = $document }
}

function Get-JarPluginIdentity([string]$Path) {
    Assert-PlainPath $Path
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $archive.GetEntry('META-INF/plugin.xml')
        if (-not $entry) { return $null }
        if ($entry.Length -gt 1048576) { throw "Plugin descriptor is too large to inspect safely: $Path" }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Read-PluginXml $text
    } finally { $archive.Dispose() }
}

function Get-AllowedTreeRecords([string]$Root, [string[]]$AllowedFiles, [switch]$AllowSubset) {
    Assert-PlainPath $Root
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Plugin directory does not exist: $Root" }
    $base = Get-FullPath $Root
    $allowedDirectories = @()
    foreach ($relative in $AllowedFiles) {
        $parent = [IO.Path]::GetDirectoryName($relative.Replace('/', '\'))
        while ($parent) {
            $allowedDirectories += $parent.Replace('\', '/')
            $parent = [IO.Path]::GetDirectoryName($parent)
        }
    }
    $records = @()
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($base)
    while ($queue.Count) {
        foreach ($item in Get-ChildItem -LiteralPath ($queue.Dequeue()) -Force) {
            Assert-UnderRoot $item.FullName $base
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Foreign reparse entry in plugin directory: $($item.FullName)" }
            $relative = $item.FullName.Substring($base.Length + 1).Replace('\', '/')
            if ($item.PSIsContainer) {
                if ($relative -notin $allowedDirectories) { throw "Unexpected plugin directory: $relative" }
                $queue.Enqueue($item.FullName)
            } else {
                if ($relative -notin $AllowedFiles) { throw "Unexpected plugin file: $relative" }
                $records += [pscustomobject]@{ RelativePath = $relative; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash; Length = $item.Length }
            }
        }
    }
    if (-not $AllowSubset -and @(Compare-Object -ReferenceObject $AllowedFiles -DifferenceObject @($records.RelativePath)).Count) {
        throw "Missing allowed plugin files in $Root"
    }
    $records
}

function Assert-FileHashes($Actual, $Expected) {
    foreach ($record in $Expected) {
        $match = @($Actual | Where-Object RelativePath -EQ $record.RelativePath)
        if ($match.Count -ne 1 -or $match[0].Sha256 -ne $record.Sha256) { throw "Plugin checksum mismatch: $($record.RelativePath)" }
    }
}

function Assert-UniquePluginId([string]$PluginsRoot, [string]$Target, [string]$PluginId) {
    Assert-PlainPath $PluginsRoot
    $targetFull = Get-FullPath $Target
    foreach ($item in Get-ChildItem -LiteralPath $PluginsRoot -Force) {
        if ((Get-FullPath $item.FullName) -eq $targetFull) { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Cannot confirm plugin identity through a reparse entry: $($item.FullName)" }
        $jars = @()
        $xmlFiles = @()
        if ($item.PSIsContainer) {
            $jars += @(Get-ChildItem -LiteralPath $item.FullName -Filter '*.jar' -File)
            $lib = Join-Path $item.FullName 'lib'
            if (Test-Path -LiteralPath $lib) {
                Assert-PlainPath $lib
                $jars += @(Get-ChildItem -LiteralPath $lib -Filter '*.jar' -File)
            }
            $directXml = Join-Path $item.FullName 'META-INF\plugin.xml'
            if (Test-Path -LiteralPath $directXml -PathType Leaf) { $xmlFiles += $directXml }
        } elseif ($item.Extension -eq '.jar') { $jars += $item }
        foreach ($jar in $jars) {
            $identity = Get-JarPluginIdentity $jar.FullName
            if ($identity -and $identity.Id -eq $PluginId) { throw "Another installed path has the same plugin ID; refusing replacement: $($item.FullName)" }
        }
        foreach ($xmlFile in $xmlFiles) {
            Assert-PlainPath $xmlFile
            $identity = Read-PluginXml ([IO.File]::ReadAllText($xmlFile))
            if ($identity -and $identity.Id -eq $PluginId) { throw "Another installed directory has the same plugin ID; refusing replacement: $($item.FullName)" }
        }
    }
}

function Copy-KnownFiles([string]$From, [string]$To, $Records) {
    Assert-PlainPath $From
    Assert-PlainPath $To
    foreach ($record in $Records) {
        $source = Join-Path $From $record.RelativePath.Replace('/', '\')
        $destination = Join-Path $To $record.RelativePath.Replace('/', '\')
        Assert-UnderRoot $source $From
        Assert-UnderRoot $destination $To
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination
    }
}

function Get-DisabledState([string]$Path, [string]$PluginId) {
    Assert-PlainPath $Path
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{ Exists = $false; Sha256 = $null; FixDisabled = $false } }
    [pscustomobject]@{ Exists = $true; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash; FixDisabled = @(Get-Content -LiteralPath $Path | Where-Object { $_.Trim() -eq $PluginId }).Count -gt 0 }
}

function Write-InstallRecord([string]$Path, $Record) {
    Assert-PlainPath $Path
    [IO.File]::WriteAllText($Path, ($Record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

$taskRoot = Get-FullPath $PSScriptRoot
if ($taskRoot -ne 'E:\Softwares\RiderVcxFix\unified-plugin') { throw 'Use install.ps1 from E:\Softwares\RiderVcxFix\unified-plugin only' }
Assert-PlainPath $taskRoot
$taskPluginId = 'local.rider.vcx-filter-view-fix'
$taskPluginVersion = '2.0.2'
$taskBuild = '263.7307.258'
$taskRiderHome = Join-Path $env:LOCALAPPDATA 'Programs\Rider 3'
$taskProduct = Get-Content -LiteralPath (Join-Path $taskRiderHome 'product-info.json') -Raw | ConvertFrom-Json
if ($taskProduct.productCode -ne 'RD' -or $taskProduct.buildNumber -ne $taskBuild) { throw "Expected RD-$taskBuild; found $($taskProduct.productCode)-$($taskProduct.buildNumber)" }
Assert-NoRiderProcesses
$taskPlugins = Join-Path $env:APPDATA 'JetBrains\Rider2026.3\plugins'
Assert-PlainPath $taskPlugins
if (-not (Test-Path -LiteralPath $taskPlugins -PathType Container)) { throw "Normal Rider plugins directory is missing: $taskPlugins" }
$taskTarget = Join-Path $taskPlugins 'rider-vcx-filter-view-fix'
Assert-UnderRoot $taskTarget $taskPlugins
Assert-PlainPath $taskTarget
Assert-UniquePluginId $taskPlugins $taskTarget $taskPluginId
$taskBundle = Join-Path $taskRoot 'bundle\rider-vcx-filters-fix'
$taskAllowed = @('lib/vcx-filter-view-fix.jar', 'dotnet/RiderVcxFix.Backend.dll', 'dotnet/0Harmony.dll')
$taskPackage = Get-Content -LiteralPath (Join-Path $taskRoot 'artifacts\package-check.json') -Raw | ConvertFrom-Json
if ($taskPackage.pluginId -ne $taskPluginId -or $taskPackage.pluginVersion -ne $taskPluginVersion -or $taskPackage.build -ne $taskBuild) { throw 'Package identity/build mismatch' }
$taskExpected = @($taskPackage.entries | ForEach-Object {
    if (-not $_.entry.StartsWith('rider-vcx-filters-fix/', [StringComparison]::Ordinal)) { throw 'Unexpected package entry prefix' }
    [pscustomobject]@{ RelativePath = $_.entry.Substring('rider-vcx-filters-fix/'.Length); Sha256 = $_.sha256 }
})
if ($taskExpected.Count -ne 3 -or @(Compare-Object -ReferenceObject $taskAllowed -DifferenceObject @($taskExpected.RelativePath)).Count) { throw 'Package must contain exactly the three approved files' }
$taskSourceFiles = @(Get-AllowedTreeRecords $taskBundle $taskAllowed)
Assert-FileHashes $taskSourceFiles $taskExpected
$taskSourceIdentity = Get-JarPluginIdentity (Join-Path $taskBundle 'lib\vcx-filter-view-fix.jar')
if ($taskSourceIdentity.Id -ne $taskPluginId -or $taskSourceIdentity.Version -ne $taskPluginVersion) { throw 'Source JAR has an unexpected plugin ID/version' }
$taskZip = Join-Path $taskRoot 'artifacts\rider-vcx-filters-fix-2.0.2.zip'
Assert-PlainPath $taskZip
$taskZipHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash
if ($taskZipHash -ne $taskPackage.zipSha256) { throw 'ZIP checksum differs from package-check.json' }
$taskOldFiles = @()
$taskOldVersion = $null
if (Test-Path -LiteralPath $taskTarget) {
    $taskOldIdentity = Get-JarPluginIdentity (Join-Path $taskTarget 'lib\vcx-filter-view-fix.jar')
    if (-not $taskOldIdentity -or $taskOldIdentity.Id -ne $taskPluginId) { throw 'The exact replacement directory is not the expected plugin' }
    $taskOldVersion = $taskOldIdentity.Version
    if ($taskOldVersion -eq '1.0.0') {
        $taskOldFiles = @(Get-AllowedTreeRecords $taskTarget @('lib/vcx-filter-view-fix.jar'))
        if ($taskOldFiles[0].Sha256 -ne '04D804D4E5FA31B1262A3B071D4FC5F0ED1D9F46416C97DCD810E238248DDE16') { throw 'Old 1.0.0 plugin differs from the verified original; refusing to overwrite it' }
    } elseif ($taskOldVersion -eq '2.0.0') {
        $taskOldFiles = @(Get-AllowedTreeRecords $taskTarget $taskAllowed)
        $taskKnown200 = @{
            'dotnet/0Harmony.dll' = 'F7D167B2500808C6C2AD6E38B6EFA2741A2B809F79B0E92C36E4A7079C38D9BB'
            'dotnet/RiderVcxFix.Backend.dll' = '7E9D53A86C4CA8F0491A65898177687BCB8FE7C060909D9CF70069EE9D4D1A61'
            'lib/vcx-filter-view-fix.jar' = '81901309CA414F2549C83BE729CDC966A487B719BF3256618A06D19700E6A085'
        }
        foreach ($taskOldFile in $taskOldFiles) {
            if ($taskKnown200[$taskOldFile.RelativePath] -ne $taskOldFile.Sha256) { throw "Old 2.0.0 file differs from the verified release: $($taskOldFile.RelativePath)" }
        }
    } elseif ($taskOldVersion -eq '2.0.1') {
        $taskOldFiles = @(Get-AllowedTreeRecords $taskTarget $taskAllowed)
        $taskKnown201 = @{
            'dotnet/0Harmony.dll' = 'F7D167B2500808C6C2AD6E38B6EFA2741A2B809F79B0E92C36E4A7079C38D9BB'
            'dotnet/RiderVcxFix.Backend.dll' = 'A6F62DFF60C2A05C6D0F7927306AC18C52D65D1018975EA98626A3F7D4A80E7A'
            'lib/vcx-filter-view-fix.jar' = '06A9EB1DEE3AF3299EEB959BD16083D68076722A8752F7494D60971AB04657BE'
        }
        foreach ($taskOldFile in $taskOldFiles) {
            if ($taskKnown201[$taskOldFile.RelativePath] -ne $taskOldFile.Sha256) { throw "Old 2.0.1 file differs from the verified release: $($taskOldFile.RelativePath)" }
        }
    } elseif ($taskOldVersion -eq $taskPluginVersion) {
        $taskOldFiles = @(Get-AllowedTreeRecords $taskTarget $taskAllowed)
        Assert-FileHashes $taskOldFiles $taskExpected
    } else { throw "Unexpected installed plugin version: $taskOldVersion" }
}
$taskDisabledFile = Join-Path (Split-Path -Parent $taskPlugins) 'disabled_plugins.txt'
$taskDisabledBefore = Get-DisabledState $taskDisabledFile $taskPluginId
$taskPlan = [ordered]@{ Status = 'VerifiedOnly'; TimestampUtc = [DateTimeOffset]::UtcNow.ToString('o'); RiderBuild = $taskBuild; PluginId = $taskPluginId; PluginVersion = $taskPluginVersion; SourceBundle = $taskBundle; Target = $taskTarget; OldVersion = $taskOldVersion; SourceFiles = $taskSourceFiles; OldFiles = $taskOldFiles; DisabledPlugins = $taskDisabledBefore; ZipSha256 = $taskZipHash }
if ($VerifyOnly) { $taskPlan | ConvertTo-Json -Depth 8; return }

$taskLockPath = Join-Path $taskRoot 'install.lock'
Assert-PlainPath $taskLockPath
$taskLock = [IO.File]::Open($taskLockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$taskBackup = $null
$taskPreviousCopy = $null
$taskMoveAttempted = $false
$taskTargetTouched = $false
$taskRecord = [ordered]@{}
foreach ($key in $taskPlan.Keys) { $taskRecord[$key] = $taskPlan[$key] }
try {
    Assert-NoRiderProcesses
    Assert-UniquePluginId $taskPlugins $taskTarget $taskPluginId
    if ($taskOldVersion -eq $taskPluginVersion) {
        $taskRecord.Status = 'AlreadyInstalled'
        Write-InstallRecord (Join-Path $taskRoot 'install.json') $taskRecord
        $taskRecord | ConvertTo-Json -Depth 8
        return
    }
    $taskBackups = Join-Path $taskRoot 'backups'
    Assert-PlainPath $taskBackups
    $taskStamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $taskBackup = Join-Path $taskBackups $taskStamp
    Assert-UnderRoot $taskBackup $taskBackups
    if (Test-Path -LiteralPath $taskBackup) { throw 'Backup path already exists' }
    New-Item -ItemType Directory -Path $taskBackup | Out-Null
    $taskStaged = Join-Path $taskBackup 'verified-new'
    Copy-KnownFiles $taskBundle $taskStaged $taskSourceFiles
    Assert-FileHashes @(Get-AllowedTreeRecords $taskStaged $taskAllowed) $taskExpected
    $taskRecord.Backup = $taskBackup
    if ($taskOldFiles.Count) {
        $taskPreviousCopy = Join-Path $taskBackup 'verified-previous'
        Copy-KnownFiles $taskTarget $taskPreviousCopy $taskOldFiles
        Assert-FileHashes @(Get-AllowedTreeRecords $taskPreviousCopy @($taskOldFiles.RelativePath)) $taskOldFiles
        $taskRecord.VerifiedPreviousCopy = $taskPreviousCopy
    }
    Write-InstallRecord (Join-Path $taskBackup 'plan.json') $taskRecord
    Assert-NoRiderProcesses
    Assert-PlainPath $taskTarget
    if ($taskOldFiles.Count) {
        Assert-FileHashes @(Get-AllowedTreeRecords $taskTarget @($taskOldFiles.RelativePath)) $taskOldFiles
        $taskOriginalMoved = Join-Path $taskBackup 'previous-original'
        Assert-UnderRoot $taskOriginalMoved $taskBackup
        $taskMoveAttempted = $true
        Move-Item -LiteralPath $taskTarget -Destination $taskOriginalMoved
        $taskRecord.MovedPreviousDirectory = $taskOriginalMoved
    } elseif (Test-Path -LiteralPath $taskTarget) { throw 'Target appeared after validation; refusing installation' }
    $taskTargetTouched = $true
    Copy-KnownFiles $taskStaged $taskTarget $taskSourceFiles
    $taskInstalled = @(Get-AllowedTreeRecords $taskTarget $taskAllowed)
    Assert-FileHashes $taskInstalled $taskExpected
    Assert-UniquePluginId $taskPlugins $taskTarget $taskPluginId
    $taskDisabledAfter = Get-DisabledState $taskDisabledFile $taskPluginId
    if ($taskDisabledAfter.Exists -ne $taskDisabledBefore.Exists -or $taskDisabledAfter.Sha256 -ne $taskDisabledBefore.Sha256) { throw 'disabled_plugins.txt changed concurrently; the installer did not write it' }
    $taskRecord.Status = 'Installed'
    $taskRecord.CompletedTimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $taskRecord.InstalledFiles = $taskInstalled
    $taskRecord.DisabledPluginsAfter = $taskDisabledAfter
    $taskRecord.DisabledPluginsUnchanged = $true
    Write-InstallRecord (Join-Path $taskBackup 'install.json') $taskRecord
    Write-InstallRecord (Join-Path $taskRoot 'install.json') $taskRecord
    $taskRecord | ConvertTo-Json -Depth 8
} catch {
    $taskFailure = $_.Exception.Message
    $taskRecord.Status = 'Failed'
    $taskRecord.Error = $taskFailure
    if ($taskMoveAttempted -or $taskTargetTouched) {
        try {
            Assert-NoRiderProcesses
            Assert-UnderRoot $taskTarget $taskPlugins
            Assert-PlainPath $taskTarget
            if (Test-Path -LiteralPath $taskTarget) {
                Get-AllowedTreeRecords $taskTarget $taskAllowed -AllowSubset | Out-Null
                $taskFailedTree = Join-Path $taskBackup 'failed-target'
                Assert-UnderRoot $taskFailedTree $taskBackup
                Move-Item -LiteralPath $taskTarget -Destination $taskFailedTree
                $taskRecord.PreservedFailedTarget = $taskFailedTree
            }
            if ($taskPreviousCopy) {
                Copy-KnownFiles $taskPreviousCopy $taskTarget $taskOldFiles
                Assert-FileHashes @(Get-AllowedTreeRecords $taskTarget @($taskOldFiles.RelativePath)) $taskOldFiles
                $taskRecord.PreviousPluginRestored = $true
            }
        } catch { $taskRecord.RollbackError = $_.Exception.Message }
    }
    if ($taskBackup) { Write-InstallRecord (Join-Path $taskBackup 'install.json') $taskRecord }
    Write-InstallRecord (Join-Path $taskRoot 'install.json') $taskRecord
    throw "Installation failed: $taskFailure. Recovery details: $(Join-Path $taskRoot 'install.json')"
} finally { $taskLock.Dispose() }
