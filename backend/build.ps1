#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$RiderHostDir = 'C:\Users\chenxin47\AppData\Local\Programs\Rider 3\lib\ReSharperHost',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$vcxBackendRoot = $PSScriptRoot
$vcxHostRoot = (Resolve-Path -LiteralPath $RiderHostDir).Path
$vcxProductInfoPath = Join-Path (Split-Path -Parent (Split-Path -Parent $vcxHostRoot)) 'product-info.json'
$vcxBuildNumber = 'unknown'
if (Test-Path -LiteralPath $vcxProductInfoPath) {
    $vcxBuildNumber = (Get-Content -LiteralPath $vcxProductInfoPath -Raw | ConvertFrom-Json).buildNumber
    if (-not $vcxBuildNumber.StartsWith('263.', [System.StringComparison]::Ordinal)) {
        throw "This plugin targets Rider 2026.3 (263.*); supplied Rider build is $vcxBuildNumber."
    }
}
$vcxApiNames = @(
    'JetBrains.Platform.Shell.dll',
    'JetBrains.Platform.Core.dll',
    'JetBrains.Platform.ComponentModel.dll',
    'JetBrains.Platform.Util.dll',
    'JetBrains.Platform.ProjectModel.dll',
    'JetBrains.Platform.ProjectModel.Host.dll',
    'JetBrains.Platform.DocumentManager.dll',
    'JetBrains.Platform.RdProtocol.dll',
    'JetBrains.RdFramework.dll',
    'JetBrains.RdBackend.Common.dll',
    'JetBrains.ReSharper.Feature.Services.dll',
    'JetBrains.ReSharperCpp.RiderPlugin.dll',
    'JetBrains.Lifetimes.dll',
    'Rider.Backend.dll',
    'JetBrains.ReSharperAutomationTools.CommandLine.Common.dll'
)
foreach ($vcxApiName in $vcxApiNames) {
    if (!(Test-Path -LiteralPath (Join-Path $vcxHostRoot $vcxApiName))) {
        throw "Missing Rider API reference: $vcxApiName"
    }
}
$vcxHarmonyPath = Join-Path $vcxBackendRoot 'lib\0Harmony.dll'
if ((Get-FileHash -LiteralPath $vcxHarmonyPath -Algorithm SHA256).Hash -ne 'F7D167B2500808C6C2AD6E38B6EFA2741A2B809F79B0E92C36E4A7079C38D9BB') {
    throw 'The checked-in Harmony binary differs from the verified 2.4.3 dependency.'
}
$vcxArtifacts = Join-Path $vcxBackendRoot 'artifacts'
New-Item -ItemType Directory -Path $vcxArtifacts -Force | Out-Null
& dotnet build (Join-Path $vcxBackendRoot 'RiderVcxFix.Backend.csproj') -c $Configuration -o $vcxArtifacts --nologo "-p:RiderHostDir=$vcxHostRoot"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
$vcxRequiredFiles = @('RiderVcxFix.Backend.dll', '0Harmony.dll')
$vcxPackaged = foreach ($vcxRequiredFile in $vcxRequiredFiles) {
    $vcxArtifactPath = Join-Path $vcxArtifacts $vcxRequiredFile
    $vcxAssemblyIdentity = [System.Reflection.AssemblyName]::GetAssemblyName($vcxArtifactPath)
    [ordered]@{
        file = $vcxRequiredFile
        assemblyName = $vcxAssemblyIdentity.Name
        assemblyVersion = $vcxAssemblyIdentity.Version.ToString()
        sha256 = (Get-FileHash -LiteralPath $vcxArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $vcxArtifactPath).Length
    }
}
$vcxHostReferences = foreach ($vcxApiName in $vcxApiNames) {
    $vcxReferencePath = Join-Path $vcxHostRoot $vcxApiName
    [ordered]@{
        file = $vcxApiName
        source = 'target Rider installation; host provides this API'
        sha256 = (Get-FileHash -LiteralPath $vcxReferencePath -Algorithm SHA256).Hash.ToLowerInvariant()
        package = $false
    }
}
[ordered]@{
    targetBuild = "RD-$vcxBuildNumber"
    targetFramework = 'net10.0'
    pluginVersion = '2.1.1'
    pluginDotnetFiles = @($vcxPackaged)
    hostReferences = @($vcxHostReferences)
    notes = @(
        'Only pluginDotnetFiles belong in the plugin dotnet directory.',
        'Rider API DLLs, PDBs, build records and test files must not be packaged.',
        'No runtimeconfig/deps JSON is required for this library loaded by Rider.',
        'No startup hook or backend environment provider is used.'
    )
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $vcxArtifacts 'dependencies.json') -Encoding utf8
Write-Host "Backend ready (RD-$vcxBuildNumber): $vcxArtifacts"
