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
$taskPluginVersion = '2.1.1'
$taskInfo = Get-Content -LiteralPath (Join-Path $RiderHome 'product-info.json') -Raw | ConvertFrom-Json
if ($taskInfo.productCode -ne 'RD' -or -not $taskInfo.buildNumber.StartsWith('263.', [System.StringComparison]::Ordinal)) {
    throw "This plugin targets Rider 2026.3 (RD-263.*); found $($taskInfo.productCode)-$($taskInfo.buildNumber)."
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
if (Test-Path -LiteralPath $taskClasses) { Remove-Item -LiteralPath $taskClasses -Recurse -Force }
if (Test-Path -LiteralPath $taskTestClasses) { Remove-Item -LiteralPath $taskTestClasses -Recurse -Force }
New-Item -ItemType Directory -Path $taskBuild, $taskClasses, $taskTestClasses, $taskLib -Force | Out-Null

$taskSources = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src') -Filter '*.java' -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName })
if ($taskSources.Count -eq 0) { throw 'No Java source files found under frontend/src.' }
& $taskJavac --release 21 -encoding UTF-8 -classpath $taskClasspath -d $taskClasses @taskSources
if ($LASTEXITCODE -ne 0) { throw "javac failed: $LASTEXITCODE" }
$taskPluginJar = Join-Path $taskLib 'vcx-filter-view-fix.jar'
Add-Type -AssemblyName System.IO.Compression
function New-TaskArchive([string]$TaskPath, [object[]]$TaskEntries) {
    $taskArchiveStream = [System.IO.File]::Open($TaskPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $taskArchive = [System.IO.Compression.ZipArchive]::new($taskArchiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskFile in ($TaskEntries | Sort-Object entry)) {
            $taskEntry = $taskArchive.CreateEntry($taskFile.entry, [System.IO.Compression.CompressionLevel]::Optimal)
            $taskEntry.LastWriteTime = [DateTimeOffset]::new(2026, 11, 20, 0, 0, 0, [TimeSpan]::Zero)
            $taskEntryStream = $taskEntry.Open()
            $taskInputStream = [System.IO.File]::OpenRead($taskFile.source)
            try { $taskInputStream.CopyTo($taskEntryStream) }
            finally { $taskInputStream.Dispose(); $taskEntryStream.Dispose() }
        }
    }
    finally { $taskArchive.Dispose(); $taskArchiveStream.Dispose() }
}
$taskClassEntries = @(Get-ChildItem -LiteralPath $taskClasses -Filter '*.class' -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($taskClasses.Length + 1).Replace('\', '/')
    @{ source = $_.FullName; entry = $rel }
})
$taskJarEntries = $taskClassEntries + @(
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
        Copy-Item -LiteralPath $taskBackendSource -Destination $taskBackendTarget -Force
        $taskBackendItems += @{ source = $taskBackendTarget; entry = 'rider-vcx-filters-fix/dotnet/' + $taskBackendName }
    }
    $taskZip = Join-Path $taskArtifacts "rider-vcx-filters-fix-$taskPluginVersion.zip"
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
$taskFixtureResult = 'not run'
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
    pluginVersion = $taskPluginVersion
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
