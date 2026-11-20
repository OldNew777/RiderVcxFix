param(
    [string]$RiderHome = 'C:\Users\chenxin47\AppData\Local\Programs\Rider 3',
    [string]$JavaHome = '',
    [switch]$RunFixture,
    [switch]$FrontendOnly,
    [string]$BackendArtifacts = ''
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskUnified = Split-Path -Parent $taskRoot
$taskArtifacts = Join-Path $taskUnified 'artifacts'
if ([string]::IsNullOrWhiteSpace($BackendArtifacts)) { $BackendArtifacts = Join-Path $taskUnified 'backend\artifacts' }
$taskBuild = Join-Path $taskRoot 'build'
$taskBundle = Join-Path $taskUnified 'bundle\rider-vcx-filters-fix'
$taskExpectedBuild = '263.7307.258'
$taskInfo = Get-Content -LiteralPath (Join-Path $RiderHome 'product-info.json') -Raw | ConvertFrom-Json
if ($taskInfo.productCode -ne 'RD' -or $taskInfo.buildNumber -ne $taskExpectedBuild) {
    throw "This plugin is reviewed for Rider RD-$taskExpectedBuild; found $($taskInfo.productCode)-$($taskInfo.buildNumber)."
}
if ([string]::IsNullOrWhiteSpace($JavaHome)) {
    $JavaHome = Join-Path $RiderHome 'jbr'
}
$taskJavac = Join-Path $JavaHome 'bin\javac.exe'
$taskJava = Join-Path $JavaHome 'bin\java.exe'
foreach ($taskTool in @($taskJavac, $taskJava)) {
    if (-not (Test-Path -LiteralPath $taskTool)) { throw "Missing JDK tool: $taskTool" }
}
$taskClasspath = (Join-Path $RiderHome 'lib\*') + ';' + (Join-Path $RiderHome 'plugins\rider-cpp\lib\*')
$taskClasses = Join-Path $taskBuild 'classes'
$taskTestClasses = Join-Path $taskBuild 'test-classes'
$taskLib = Join-Path $taskBundle 'lib'
New-Item -ItemType Directory -Path $taskBuild, $taskClasses, $taskTestClasses, $taskLib -Force | Out-Null

$taskSource = Join-Path $taskRoot 'src\local\rider\vcxfix\VcxFilterViewFix.java'
& $taskJavac --release 21 -encoding UTF-8 -classpath $taskClasspath -d $taskClasses $taskSource
if ($LASTEXITCODE -ne 0) { throw "javac failed: $LASTEXITCODE" }
$taskPluginJar = Join-Path $taskLib 'vcx-filter-view-fix.jar'
Add-Type -AssemblyName System.IO.Compression
function New-TaskArchive([string]$TaskPath, [object[]]$TaskEntries) {
    $taskArchiveStream = [System.IO.File]::Open($TaskPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $taskArchive = [System.IO.Compression.ZipArchive]::new($taskArchiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskFile in ($TaskEntries | Sort-Object entry)) {
            $taskEntry = $taskArchive.CreateEntry($taskFile.entry, [System.IO.Compression.CompressionLevel]::Optimal)
            $taskEntry.LastWriteTime = [DateTimeOffset]::new(2026, 11, 13, 0, 0, 0, [TimeSpan]::Zero)
            $taskEntryStream = $taskEntry.Open()
            $taskInputStream = [System.IO.File]::OpenRead($taskFile.source)
            try { $taskInputStream.CopyTo($taskEntryStream) }
            finally { $taskInputStream.Dispose(); $taskEntryStream.Dispose() }
        }
    }
    finally { $taskArchive.Dispose(); $taskArchiveStream.Dispose() }
}
$taskJarEntries = @(
    @{ source = Join-Path $taskClasses 'local\rider\vcxfix\VcxFilterViewFix.class'; entry = 'local/rider/vcxfix/VcxFilterViewFix.class' },
    @{ source = Join-Path $taskRoot 'resources\META-INF\plugin.xml'; entry = 'META-INF/plugin.xml' }
)
New-TaskArchive $taskPluginJar $taskJarEntries
$taskZip = $null
$taskBackendItems = @()
if (-not $FrontendOnly) {
    $taskDotnet = Join-Path $taskBundle 'dotnet'
    New-Item -ItemType Directory -Path $taskArtifacts, $taskDotnet -Force | Out-Null
    foreach ($taskBackendName in @('RiderVcxFix.Backend.dll', '0Harmony.dll')) {
        $taskBackendSource = Join-Path $BackendArtifacts $taskBackendName
        if (-not (Test-Path -LiteralPath $taskBackendSource -PathType Leaf)) {
            throw "Missing backend artifact: $taskBackendSource. Use -FrontendOnly to compile the JAR separately."
        }
        $taskBackendTarget = Join-Path $taskDotnet $taskBackendName
        Copy-Item -LiteralPath $taskBackendSource -Destination $taskBackendTarget
        $taskBackendItems += @{ source = $taskBackendTarget; entry = 'rider-vcx-filters-fix/dotnet/' + $taskBackendName }
    }
    $taskZip = Join-Path $taskArtifacts 'rider-vcx-filters-fix-2.0.2.zip'
    $taskArchiveItems = @(@{ source = $taskPluginJar; entry = 'rider-vcx-filters-fix/lib/vcx-filter-view-fix.jar' }) + $taskBackendItems
    New-TaskArchive $taskZip $taskArchiveItems
}

$taskDependencyNames = @(
    'lib\intellij.rider.jar',
    'lib\intellij.rider.rdclient.dotnet.jar',
    'lib\intellij.rider.model.generated.jar',
    'lib\intellij.platform.editor.ui.jar',
    'lib\intellij.platform.core.jar',
    'lib\intellij.platform.workspace.storage.jar',
    'lib\util-8.jar'
)
$taskDependencies = foreach ($taskRelativePath in $taskDependencyNames) {
    $taskPath = Join-Path $RiderHome $taskRelativePath
    [ordered]@{ path = $taskPath; sha256 = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash }
}
$taskFixtureResult = 'not run; algorithm and fixture copied from previously verified 1.0.0'
if ($RunFixture) {
    $taskFixtureSource = Join-Path $taskRoot 'tests\local\rider\vcxfix\VcxFilterViewFixFixture.java'
    & $taskJavac --release 21 -encoding UTF-8 -classpath ($taskPluginJar + ';' + $taskClasspath) -d $taskTestClasses $taskFixtureSource
    if ($LASTEXITCODE -ne 0) { throw "Fixture javac failed: $LASTEXITCODE" }
    & $taskJava -classpath ($taskTestClasses + ';' + $taskPluginJar + ';' + $taskClasspath) local.rider.vcxfix.VcxFilterViewFixFixture
    if ($LASTEXITCODE -ne 0) { throw "Fixture failed: $LASTEXITCODE" }
    $taskFixtureResult = 'passed with actual Rider API classes; no IDE UI exercised'
}

$taskManifest = [ordered]@{
    pluginId = 'local.rider.vcx-filter-view-fix'
    pluginVersion = '2.0.2'
    riderVersion = $taskInfo.version
    riderBuild = $taskInfo.buildNumber
    javaReleaseTarget = 21
    javaHome = $JavaHome
    classpath = $taskClasspath
    dependencies = @($taskDependencies)
    jar = [ordered]@{ path = $taskPluginJar; sha256 = (Get-FileHash -LiteralPath $taskPluginJar -Algorithm SHA256).Hash }
    zip = $(if ($taskZip) { [ordered]@{ path = $taskZip; sha256 = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash } } else { $null })
    backend = @($taskBackendItems | ForEach-Object { [ordered]@{ path = $_.source; sha256 = (Get-FileHash -LiteralPath $_.source -Algorithm SHA256).Hash } })
    fixture = $taskFixtureResult
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRoot 'build-manifest.json') -Encoding utf8
$taskManifest | ConvertTo-Json -Depth 6
